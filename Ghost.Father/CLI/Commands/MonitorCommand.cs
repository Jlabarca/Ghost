using Ghost.Storage;
using MemoryPack;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Ghost.Father.CLI.Commands;

public class MonitorCommand : AsyncCommand<MonitorCommand.Settings>
{
  private readonly IGhostBus _bus;
  private readonly Dictionary<string, GhostProcessInfo> _processes = new();
  private readonly List<GhostProcessInfo> _processHistory = new();
  private readonly ConcurrentDictionary<string, ProcessMetrics> _latestMetrics = new();
  private readonly List<LogEntry> _logs = new();
  private readonly object _lockObject = new();

  private bool _monitoring = true;
  private DateTime _lastUpdate = DateTime.UtcNow;
  private int _totalProcesses = 0;
  private int _onlineProcesses = 0;
  private bool _daemonAlive = false;
  private string _selectedProcessId = null;
  private bool _isInitializing = true;
  private Timer _pollTimer;

  public class Settings : CommandSettings
  {
    [CommandOption("--refresh")]
    [Description("Refresh interval in milliseconds")]
    [DefaultValue(500)]
    public int RefreshInterval { get; set; }

    [CommandOption("--lines")]
    [Description("Number of log lines to show")]
    [DefaultValue(12)]
    public int LogLines { get; set; }

    [CommandOption("--history")]
    [Description("Number of historical processes to show")]
    [DefaultValue(5)]
    public int HistoryCount { get; set; }
  }

  public MonitorCommand(IGhostBus bus)
  {
    _bus = bus;
    G.LogInfo($"DAEMON CONFIRMATION - Bus Type is: {_bus.GetType().FullName}");
  }


  public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
  {
    try
    {
      // Clear screen and hide cursor
      //Console.Clear();
      //AnsiConsole.Clear();
      //AnsiConsole.Cursor.Hide();

      // Initialize data BEFORE starting the live display
      await InitializeDataWithStatusAsync();

      // Create initial dashboard
      var dashboard = CreateDashboard(settings);

      // Start background tasks and monitoring
      var cts = new CancellationTokenSource();
      Console.CancelKeyPress += (_, e) =>
      {
        e.Cancel = true;
        _monitoring = false;
        cts.Cancel();
      };

      // Use Live Display for real-time updates
      await AnsiConsole.Live(dashboard)
          .AutoClear(true) // Enable auto-clear to prevent overlapping
          .Overflow(VerticalOverflow.Ellipsis)
          .StartAsync(async ctx =>
          {
            // Start polling timer - check daemon every 10 seconds
            _pollTimer = new Timer(_ =>
            {
              try
              {
                _ = Task.Run(async () =>
                {
                  await PollProcessesAsync();
                });
              }
              catch (Exception ex)
              {
                AddLog("monitor", "error", $"Polling error: {ex.Message}");
              }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));

            // Start background tasks for monitoring
            var tasks = new List<Task>
            {
                MonitorProcessesAsync(cts.Token),
                MonitorMetricsAsync(cts.Token),
                MonitorLogsAsync(cts.Token)
            };

            // Start background task processing
            _ = Task.WhenAll(tasks);

            // Mark initialization as complete
            _isInitializing = false;

            // Main update loop
            while (_monitoring && !cts.Token.IsCancellationRequested)
            {
              try
              {
                // Update the dashboard
                var updatedDashboard = CreateDashboard(settings);
                ctx.UpdateTarget(updatedDashboard);

                await Task.Delay(settings.RefreshInterval, cts.Token);
              }
              catch (OperationCanceledException)
              {
                break;
              }
              catch (Exception ex)
              {
                // Add error to logs but continue
                AddLog("monitor", "error", $"Display error: {ex.Message}");
                await Task.Delay(1000, cts.Token);
              }
            }
          });

      return 0;
    }
    catch (Exception ex)
    {
      AnsiConsole.WriteException(ex);
      return 1;
    }
    finally
    {
      _pollTimer?.Dispose();
      //AnsiConsole.Cursor.Show();
      //Console.Clear();
    }
  }

  // Separate initialization method that uses Status display BEFORE Live display
  private async Task InitializeDataWithStatusAsync()
  {
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(Style.Parse("green"))
        .StartAsync("Initializing connection to Ghost daemon...", async ctx =>
        {
          ctx.Status("Checking daemon connection...");
          await Task.Delay(500); // Give user time to see the status

          try
          {
            // First try to ping the daemon
            await CheckDaemonConnectionAsync();

            if (_daemonAlive)
            {
              ctx.Status("Daemon connected. Discovering processes...");
              await Task.Delay(500);

              // Send discover commands
              await SendDiscoveryCommandsAsync();

              ctx.Status("Setting up monitoring...");
              await Task.Delay(300);

              AddLog("monitor", "info", "Monitor initialized successfully");
            } else
            {
              ctx.Status("Daemon offline. Monitoring in standalone mode...");
              await Task.Delay(1000);

              AddLog("monitor", "warn", "Daemon not responding - running in standalone mode");
            }
          }
          catch (Exception ex)
          {
            ctx.Status($"Initialization error: {ex.Message}");
            await Task.Delay(1500);

            AddLog("monitor", "error", $"Initialization failed: {ex.Message}");
          }
        });
  }

  private async Task CheckDaemonConnectionAsync()
  {
    try
    {
      AddLog("monitor", "debug", "Starting daemon connection check...");

      var pingCommand = new SystemCommand
      {
          CommandId = Guid.NewGuid().ToString(),
          CommandType = "ping",
          Parameters = new Dictionary<string, string>
          {
              ["responseChannel"] = $"ghost:responses:{Guid.NewGuid()}"
          }
      };

      var responseChannel = pingCommand.Parameters["responseChannel"];
      AddLog("monitor", "debug", $"Created ping command {pingCommand.CommandId}, response channel: {responseChannel}");

      // Create a task to wait for response
      var responseTask = Task.Run(async () =>
      {
        try
        {
          AddLog("monitor", "debug", $"Subscribing to response channel: {responseChannel}");
          using var cts = new CancellationTokenSource(5000); // 5 second timeout

          await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
          {
            AddLog("monitor", "debug", $"Received response: CommandId={response?.CommandId}, Success={response?.Success}");

            if (response != null && response.CommandId == pingCommand.CommandId && response.Success)
            {
              AddLog("monitor", "info", "Daemon ping successful!");
              return true;
            } else
            {
              AddLog("monitor", "warn", $"Received invalid response: CommandId mismatch or failed");
            }
          }

          AddLog("monitor", "warn", "No valid response received from daemon");
          return false;
        }
        catch (OperationCanceledException)
        {
          AddLog("monitor", "warn", "Daemon ping timed out after 5 seconds");
          return false;
        }
        catch (Exception ex)
        {
          AddLog("monitor", "error", $"Error waiting for daemon response: {ex.Message}");
          return false;
        }
      });

      // Send the ping command
      AddLog("monitor", "debug", "Publishing ping command to ghost:commands channel...");
      await _bus.PublishAsync("ghost:commands", pingCommand);
      AddLog("monitor", "debug", "Ping command published, waiting for response...");

      // Wait for response with timeout
      _daemonAlive = await responseTask;

      AddLog("monitor", "info", $"Daemon connection check completed: {(_daemonAlive ? "ALIVE" : "DEAD")}");
    }
    catch (Exception ex)
    {
      AddLog("monitor", "error", $"Exception during daemon connection check: {ex.Message}");
      _daemonAlive = false;
    }
  }

  private async Task SendDiscoveryCommandsAsync()
  {
    try
    {
      // Send discover command
      var discoverCommand = new SystemCommand
      {
          CommandId = Guid.NewGuid().ToString(),
          CommandType = "discover",
          Parameters = new Dictionary<string, string>()
      };

      await _bus.PublishAsync("ghost:commands", discoverCommand);
      await Task.Delay(500);

      // Send list command for backward compatibility
      var listCommand = new SystemCommand
      {
          CommandId = Guid.NewGuid().ToString(),
          CommandType = "list",
          Parameters = new Dictionary<string, string>()
      };

      await _bus.PublishAsync("ghost:commands", listCommand);
      await Task.Delay(300);

      // Publish a query event for discovery
      await _bus.PublishAsync("ghost:events", new
      {
          Id = "monitor-query",
          EventType = "query_processes",
          Timestamp = DateTime.UtcNow
      });
    }
    catch (Exception ex)
    {
      AddLog("monitor", "warn", $"Discovery command failed: {ex.Message}");
    }
  }

  private Layout CreateDashboard(Settings settings)
  {
    var layout = new Layout("Root")
        .SplitRows(
            new Layout("Header").Size(3),
            new Layout("Content").SplitColumns(
                new Layout("MainPanel").SplitRows(
                    new Layout("ProcessPanel").Ratio(2),
                    new Layout("HistoryPanel").Ratio(1)
                ).Ratio(3),
                new Layout("SidePanel").SplitRows(
                    new Layout("DetailPanel").Ratio(1),
                    new Layout("LogPanel").Ratio(2)
                ).Ratio(2)
            ),
            new Layout("Footer").SplitColumns(
                new Layout("Metrics").Ratio(1),
                new Layout("Controls").Ratio(1)
            ).Size(8)
        );

    layout["Header"].Update(RenderHeader());
    layout["ProcessPanel"].Update(RenderActiveProcessTable(settings));
    layout["HistoryPanel"].Update(RenderHistoryTable(settings));
    layout["DetailPanel"].Update(RenderProcessDetails());
    layout["LogPanel"].Update(RenderLogs(settings));
    layout["Metrics"].Update(RenderMetrics());
    layout["Controls"].Update(RenderControls());

    return layout;
  }

  private Panel RenderHeader()
  {
    var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime;

    // Show initialization status
    if (_isInitializing)
    {
      var content1 = new Markup("[yellow]Initializing Ghost Monitor...[/]").Centered();
      return new Panel(content1)
          .Border(BoxBorder.Rounded)
          .BorderColor(Color.Yellow)
          .Header("[bold]Ghost Process Manager[/]");
    }

    // Different status indicators based on daemon and process status
    string statusText, statusColor;
    if (_daemonAlive)
    {
      if (_onlineProcesses > 0)
      {
        statusText = "Online";
        statusColor = "green";
      } else
      {
        statusText = "Idle";
        statusColor = "yellow";
      }
    } else
    {
      statusText = "Offline";
      statusColor = "red";
    }

    var daemonStatus = _daemonAlive ? "[green]Running[/]" : "[red]Offline[/]";

    var content = new Grid()
        .AddColumn(new GridColumn().NoWrap())
        .AddColumn(new GridColumn().NoWrap())
        .AddColumn(new GridColumn().NoWrap())
        .AddRow(
            $"[bold blue]Ghost Father Monitor[/]",
            $"Status: [{statusColor}]{statusText}[/]",
            $"Uptime: [yellow]{FormatDuration(uptime)}[/]"
        )
        .AddRow(
            $"Total: [blue]{_totalProcesses}[/]",
            $"Active: [green]{_onlineProcesses}[/] (Daemon: {daemonStatus})",
            $"Updated: [grey]{_lastUpdate:HH:mm:ss}[/]"
        );

    return new Panel(content)
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Blue)
        .Header("[bold]Ghost Process Manager[/]")
        .HeaderAlignment(Justify.Center);
  }

  private Table RenderActiveProcessTable(Settings settings)
  {
    var table = new Table()
        .BorderColor(Color.Green)
        .Title("[bold]Active Processes[/]")
        .Expand();

    // Add columns with better formatting
    table.AddColumn(new TableColumn("[bold]ID[/]").Centered().Width(10));
    table.AddColumn(new TableColumn("[bold]Name[/]").LeftAligned());
    table.AddColumn(new TableColumn("[bold]Type[/]").Centered().Width(8));
    table.AddColumn(new TableColumn("[bold]Status[/]").Centered().Width(10));
    table.AddColumn(new TableColumn("[bold]CPU[/]").RightAligned().Width(7));
    table.AddColumn(new TableColumn("[bold]Memory[/]").RightAligned().Width(10));
    table.AddColumn(new TableColumn("[bold]Uptime[/]").RightAligned().Width(10));

    lock (_lockObject)
    {
      // Show active processes
      var activeProcesses = _processes.Values
          .Where(p => p.Status?.ToLowerInvariant() != "stopped")
          .OrderBy(p => p.Id)
          .ToList();

      foreach (var process in activeProcesses)
      {
        var statusColor = process.Status?.ToLowerInvariant() switch
        {
            "online" => "green",
            "stopped" => "red",
            "errored" => "red",
            "stopping" => "yellow",
            "launching" => "yellow",
            _ => "grey"
        };

        var processType = process.Mode?.ToLowerInvariant() switch
        {
            "service" => "[blue]service[/]",
            "app" => "[cyan]app[/]",
            "daemon" => "[magenta]daemon[/]",
            _ => process.Mode ?? "app"
        };

        var cpuText = process.CpuUsage.HasValue ? $"{process.CpuUsage:F1}%" : "-";
        var memoryText = process.MemoryUsage.HasValue ? FormatBytes(process.MemoryUsage.Value) : "-";
        var uptimeText = process.StartTime.HasValue
            ? FormatDuration(DateTime.UtcNow - process.StartTime.Value)
            : "-";

        // Highlight selected process
        string idText = GetShortId(process.Id);
        string nameText = process.Name ?? "";

        if (process.Id == _selectedProcessId)
        {
          idText = $"[black on white]{idText}[/]";
          nameText = $"[black on white]{nameText}[/]";
        }

        table.AddRow(
            idText,
            nameText,
            processType,
            $"[{statusColor}]{process.Status}[/]",
            cpuText,
            memoryText,
            uptimeText
        );
      }

      if (!activeProcesses.Any())
      {
        table.AddRow(
            "[grey]No active processes found[/]",
            "", "", "", "", "", ""
        );
      }
    }

    return table;
  }

  private Table RenderHistoryTable(Settings settings)
  {
    var table = new Table()
        .BorderColor(Color.Blue)
        .Title("[bold]Process History[/]")
        .Expand();

    // Add columns
    table.AddColumn(new TableColumn("[bold]ID[/]").Centered().Width(10));
    table.AddColumn(new TableColumn("[bold]Name[/]").LeftAligned());
    table.AddColumn(new TableColumn("[bold]Type[/]").Centered().Width(8));
    table.AddColumn(new TableColumn("[bold]Status[/]").Centered().Width(10));
    table.AddColumn(new TableColumn("[bold]Started[/]").Centered().Width(12));
    table.AddColumn(new TableColumn("[bold]Ended[/]").Centered().Width(12));
    table.AddColumn(new TableColumn("[bold]Duration[/]").RightAligned().Width(10));

    lock (_lockObject)
    {
      // Get history processes plus stopped active processes
      var historyProcesses = _processHistory
          .Concat(_processes.Values.Where(p => p.Status?.ToLowerInvariant() == "stopped"))
          .OrderByDescending(p => p.EndTime ?? DateTime.MinValue)
          .ThenByDescending(p => p.StartTime ?? DateTime.MinValue)
          .Take(settings.HistoryCount)
          .ToList();

      foreach (var process in historyProcesses)
      {
        var statusColor = process.Status?.ToLowerInvariant() switch
        {
            "stopped" => "blue",
            "errored" => "red",
            "crashed" => "red",
            _ => "grey"
        };

        var processType = process.Mode?.ToLowerInvariant() switch
        {
            "service" => "[blue]service[/]",
            "app" => "[cyan]app[/]",
            "daemon" => "[magenta]daemon[/]",
            _ => process.Mode ?? "app"
        };

        var startText = process.StartTime?.ToString("HH:mm:ss") ?? "-";
        var endText = process.EndTime?.ToString("HH:mm:ss") ?? "-";
        var durationText = (process.StartTime.HasValue && process.EndTime.HasValue)
            ? FormatDuration(process.EndTime.Value - process.StartTime.Value)
            : "-";

        // Highlight selected process
        string idText = GetShortId(process.Id);
        string nameText = process.Name ?? "";

        if (process.Id == _selectedProcessId)
        {
          idText = $"[black on white]{idText}[/]";
          nameText = $"[black on white]{nameText}[/]";
        }

        table.AddRow(
            idText,
            nameText,
            processType,
            $"[{statusColor}]{process.Status}[/]",
            startText,
            endText,
            durationText
        );
      }

      if (!historyProcesses.Any())
      {
        table.AddRow(
            "[grey]No process history available[/]",
            "", "", "", "", "", ""
        );
      }
    }

    return table;
  }

  private Panel RenderProcessDetails()
  {
    Panel panel;

    lock (_lockObject)
    {
      GhostProcessInfo selectedProcess = null;

      // Find the selected process
      if (!string.IsNullOrEmpty(_selectedProcessId))
      {
        selectedProcess = _processes.TryGetValue(_selectedProcessId, out var process)
            ? process
            : _processHistory.FirstOrDefault(p => p.Id == _selectedProcessId);
      }

      if (selectedProcess != null)
      {
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(1))
            .AddColumn(new GridColumn().PadLeft(1));

        grid.AddRow("[bold]Process ID:[/]", selectedProcess.Id);
        grid.AddRow("[bold]Name:[/]", selectedProcess.Name ?? "-");
        grid.AddRow("[bold]Type:[/]", selectedProcess.Mode ?? "app");

        var statusColor = selectedProcess.Status?.ToLowerInvariant() switch
        {
            "online" => "green",
            "stopped" => "blue",
            "errored" => "red",
            _ => "grey"
        };

        grid.AddRow("[bold]Status:[/]", $"[{statusColor}]{selectedProcess.Status}[/]");

        if (selectedProcess.StartTime.HasValue)
        {
          grid.AddRow("[bold]Started:[/]", selectedProcess.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        if (selectedProcess.EndTime.HasValue)
        {
          grid.AddRow("[bold]Ended:[/]", selectedProcess.EndTime.Value.ToString("yyyy-MM-dd HH:mm:ss"));

          if (selectedProcess.StartTime.HasValue)
          {
            var duration = selectedProcess.EndTime.Value - selectedProcess.StartTime.Value;
            grid.AddRow("[bold]Runtime:[/]", FormatDuration(duration));
          }
        } else if (selectedProcess.StartTime.HasValue)
        {
          var uptime = DateTime.UtcNow - selectedProcess.StartTime.Value;
          grid.AddRow("[bold]Uptime:[/]", FormatDuration(uptime));
        }

        grid.AddRow("[bold]Restarts:[/]", selectedProcess.Restarts?.ToString() ?? "0");

        if (selectedProcess.CpuUsage.HasValue)
        {
          grid.AddRow("[bold]CPU Usage:[/]", $"{selectedProcess.CpuUsage:F2}%");
        }

        if (selectedProcess.MemoryUsage.HasValue)
        {
          grid.AddRow("[bold]Memory:[/]", FormatBytes(selectedProcess.MemoryUsage.Value));
        }

        panel = new Panel(grid)
            .Header($"[bold]Process Details: {selectedProcess.Name}[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Expand();
      } else
      {
        panel = new Panel(new Markup("[grey]No process selected[/]").Centered())
            .Header("[bold]Process Details[/]")
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Yellow)
            .Expand();
      }
    }

    return panel;
  }

  private Panel RenderLogs(Settings settings)
  {
    var logTable = new Table()
        .Border(TableBorder.None)
        .Expand()
        .HideHeaders();

    logTable.AddColumn(new TableColumn("[grey]Time[/]").Width(8));
    logTable.AddColumn(new TableColumn("[grey]Source[/]").Width(8));
    logTable.AddColumn(new TableColumn("[grey]Level[/]").Width(5));
    logTable.AddColumn(new TableColumn("[grey]Message[/]").NoWrap());

    lock (_lockObject)
    {
      var filteredLogs = _logs
          .Where(log => !log.Message?.Contains("Spectre.Console") ?? true)
          .TakeLast(settings.LogLines)
          .ToList();

      foreach (var log in filteredLogs)
      {
        var timeStr = log.Timestamp.ToString("HH:mm:ss");
        var sourceName = GetShortName(log.Source);

        var levelColor = log.Level?.ToLowerInvariant() switch
        {
            "error" => "red",
            "warn" or "warning" => "yellow",
            "info" => "green",
            "debug" => "grey",
            _ => "white"
        };

        var levelText = log.Level?.ToUpper();
        if (!string.IsNullOrEmpty(levelText) && levelText.Length > 4)
        {
          levelText = levelText.Substring(0, 4);
        }
        levelText = levelText ?? "INFO";

        logTable.AddRow(
            $"[grey]{timeStr}[/]",
            $"[blue]{sourceName}[/]",
            $"[{levelColor}]{levelText}[/]",
            log.Message ?? ""
        );
      }

      if (!filteredLogs.Any())
      {
        logTable.AddRow(
            "[grey]--:--:--[/]",
            "[grey]---[/]",
            "[grey]---[/]",
            "[grey]No logs available yet[/]"
        );
      }
    }

    return new Panel(logTable)
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Magenta1)
        .Header("[bold]Application Logs[/]")
        .Expand();
  }

  private Panel RenderMetrics()
  {
    var currentProcess = Process.GetCurrentProcess();

    var metrics = new Grid()
        .AddColumn(new GridColumn().NoWrap().PadRight(4))
        .AddColumn()
        .AddRow("[bold]System Metrics[/]", "")
        .AddRow("[cyan]CPU Cores:[/]", Environment.ProcessorCount.ToString())
        .AddRow("[cyan]Memory Usage:[/]", FormatBytes(currentProcess.WorkingSet64))
        .AddRow("[cyan]Threads:[/]", currentProcess.Threads.Count.ToString())
        .AddRow("[bold]GC Info[/]", "")
        .AddRow("[green]Gen 0:[/]", GC.CollectionCount(0).ToString("N0"))
        .AddRow("[green]Gen 1:[/]", GC.CollectionCount(1).ToString("N0"))
        .AddRow("[green]Gen 2:[/]", GC.CollectionCount(2).ToString("N0"));

    return new Panel(metrics)
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Green)
        .Header("[bold]System Metrics[/]")
        .Expand();
  }

  private Panel RenderControls()
  {
    var controls = new Grid()
        .AddColumn(new GridColumn().NoWrap().PadRight(4))
        .AddColumn()
        .AddRow("[bold]Controls[/]", "")
        .AddRow("[blue]Ctrl+C[/]", "Exit Monitor")
        .AddRow("[blue]↑/↓[/]", "Navigate Processes")
        .AddRow("[blue]Enter[/]", "Select Process")
        .AddRow("[bold]Commands[/]", "")
        .AddRow("[green]start[/]", "Start Process")
        .AddRow("[red]stop[/]", "Stop Process")
        .AddRow("[yellow]restart[/]", "Restart Process");

    return new Panel(controls)
        .Border(BoxBorder.Rounded)
        .BorderColor(Color.Cyan1)
        .Header("[bold]Key Bindings[/]")
        .Expand();
  }

  // Rest of the methods remain the same as before, but I'll include the critical ones:

  private async Task<T> WaitForResponseAsync<T>(string channel, CancellationToken ct)
  {
    try
    {
      await foreach (var response in _bus.SubscribeAsync<T>(channel, ct))
      {
        return response;
      }
    }
    catch (OperationCanceledException)
    {
      // Timeout is expected
    }
    return default;
  }

  private async Task MonitorProcessesAsync(CancellationToken cancellationToken)
  {
    try
    {
      AddLog("monitor", "debug", "Starting to monitor ghost:events:* channel...");

      await foreach (var message in _bus.SubscribeAsync<object>("ghost:events:*", cancellationToken))
      {
        if (!_monitoring) break;

        try
        {
          AddLog("monitor", "debug", $"Received event message: {message?.GetType().Name}");
          await ProcessEventMessage(message);
          _lastUpdate = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
          AddLog("monitor", "warn", $"Failed to process event: {ex.Message}");
        }
      }
    }
    catch (OperationCanceledException)
    {
      AddLog("monitor", "debug", "Process monitoring cancelled");
    }
    catch (Exception ex)
    {
      AddLog("monitor", "error", $"Process monitor error: {ex.Message}");
    }
  }

  private async Task MonitorMetricsAsync(CancellationToken cancellationToken)
  {
    try
    {
      AddLog("monitor", "debug", "Starting to monitor ghost:metrics:* channel...");

      await foreach (var metricsData in _bus.SubscribeAsync<object>("ghost:metrics:*", cancellationToken))
      {
        if (!_monitoring) break;

        try
        {
          var topic = _bus.GetLastTopic();
          var processId = ExtractProcessIdFromTopic(topic);

          AddLog("monitor", "debug", $"Received metrics for process: {processId}");

          if (!string.IsNullOrEmpty(processId))
          {
            var metrics = await ExtractMetricsAsync(metricsData, processId);
            if (metrics != null)
            {
              _latestMetrics[processId] = metrics;
              UpdateProcessWithMetrics(processId, metrics);
              _lastUpdate = DateTime.UtcNow;
            }
          }
        }
        catch (Exception ex)
        {
          AddLog("monitor", "debug", $"Failed to process metrics: {ex.Message}");
        }
      }
    }
    catch (OperationCanceledException)
    {
      AddLog("monitor", "debug", "Metrics monitoring cancelled");
    }
    catch (Exception ex)
    {
      AddLog("monitor", "error", $"Metrics monitor error: {ex.Message}");
    }
  }
  private async Task MonitorLogsAsync(CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var logData in _bus.SubscribeAsync<object>("ghost:logs:*", cancellationToken))
      {
        if (!_monitoring) break;

        try
        {
          ProcessLogMessage(logData);
          _lastUpdate = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
          AddLog("monitor", "debug", $"Failed to process log: {ex.Message}");
        }
      }
    }
    catch (OperationCanceledException)
    {
      // Expected when cancelling
    }
    catch (Exception ex)
    {
      AddLog("monitor", "error", $"Log monitor error: {ex.Message}");
    }
  }

  private async Task PollProcessesAsync()
  {
    try
    {
      AddLog("monitor", "debug", "Starting daemon polling...");

      // Send a ping command to check if daemon is alive
      var pingCommand = new SystemCommand
      {
          CommandId = Guid.NewGuid().ToString(),
          CommandType = "ping",
          Parameters = new Dictionary<string, string>
          {
              ["responseChannel"] = $"ghost:responses:{Guid.NewGuid()}"
          }
      };

      var responseChannel = pingCommand.Parameters["responseChannel"];
      AddLog("monitor", "debug", $"Polling with ping command {pingCommand.CommandId}");

      // Create a task to wait for response
      var responseTask = Task.Run(async () =>
      {
        try
        {
          using var cts = new CancellationTokenSource(3000); // 3 second timeout

          await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
          {
            if (response != null && response.CommandId == pingCommand.CommandId && response.Success)
            {
              return true;
            }
          }
          return false;
        }
        catch (OperationCanceledException)
        {
          return false;
        }
        catch (Exception)
        {
          return false;
        }
      });

      // Send the ping command
      await _bus.PublishAsync("ghost:commands", pingCommand);

      // Wait for response
      bool wasDaemonAlive = _daemonAlive;
      _daemonAlive = await responseTask;

      if (wasDaemonAlive != _daemonAlive)
      {
        if (_daemonAlive)
          AddLog("monitor", "info", "Daemon connection established");
        else
          AddLog("monitor", "warn", "Daemon connection lost");
      }

      // If no processes selected but some exist, select the first one
      if (string.IsNullOrEmpty(_selectedProcessId) && _processes.Count > 0)
      {
        _selectedProcessId = _processes.Keys.FirstOrDefault();
      }

      AddLog("monitor", "debug", $"Polling completed: daemon={_daemonAlive}, processes={_processes.Count}");
    }
    catch (Exception ex)
    {
      AddLog("monitor", "error", $"Error in process polling: {ex.Message}");
    }
  }


  private async Task ProcessEventMessage(object message)
  {
    try
    {
      var processInfo = await ExtractProcessInfoFromEvent(message);
      if (processInfo != null)
      {
        UpdateProcess(processInfo);
      }
    }
    catch (Exception ex)
    {
      AddLog("monitor", "debug", $"Failed to process event message: {ex.Message}");
    }
  }

  private async Task<GhostProcessInfo> ExtractProcessInfoFromEvent(object message)
  {
    try
    {
      var json = JsonSerializer.Serialize(message);
      var eventData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

      if (eventData == null) return null;

      if (!eventData.TryGetValue("Id", out var idElement) ||
          idElement.ValueKind == JsonValueKind.Undefined)
        return null;

      var id = idElement.ToString();
      if (string.IsNullOrEmpty(id))
        return null;

      string name = null;
      if (eventData.TryGetValue("ProcessName", out var nameElement) &&
          nameElement.ValueKind != JsonValueKind.Undefined)
        name = nameElement.ToString();
      else if (eventData.TryGetValue("Name", out nameElement) &&
               nameElement.ValueKind != JsonValueKind.Undefined)
        name = nameElement.ToString();

      string status = null;
      if (eventData.TryGetValue("Status", out var statusElement) &&
          statusElement.ValueKind != JsonValueKind.Undefined)
        status = statusElement.ToString();

      if (string.IsNullOrEmpty(status) && eventData.TryGetValue("EventType", out var eventTypeElement) &&
          eventTypeElement.ValueKind != JsonValueKind.Undefined)
      {
        var eventType = eventTypeElement.ToString().ToLowerInvariant();
        status = eventType switch
        {
            "process_started" or "process_registered" or "process_discovered" => "online",
            "process_stopped" => "stopped",
            "process_failed" or "process_error" => "errored",
            _ => "unknown"
        };
      }

      string mode = null;
      if (eventData.TryGetValue("Mode", out var modeElement) &&
          modeElement.ValueKind != JsonValueKind.Undefined)
        mode = modeElement.ToString();
      else if (eventData.TryGetValue("ProcessType", out modeElement) &&
               modeElement.ValueKind != JsonValueKind.Undefined)
        mode = modeElement.ToString();
      else if (eventData.TryGetValue("Type", out modeElement) &&
               modeElement.ValueKind != JsonValueKind.Undefined)
        mode = modeElement.ToString();

      return new GhostProcessInfo
      {
          Id = id,
          Name = name ?? GetDisplayName(id),
          Status = status ?? "unknown",
          Mode = mode ?? "app",
          StartTime = DateTime.UtcNow,
          User = Environment.UserName
      };
    }
    catch (Exception ex)
    {
      AddLog("monitor", "debug", $"Failed to extract process info: {ex.Message}");
      return null;
    }
  }

  private void UpdateProcess(GhostProcessInfo processInfo)
  {
    lock (_lockObject)
    {
      var existing = _processes.TryGetValue(processInfo.Id, out var current);

      if (!existing)
      {
        _processes[processInfo.Id] = processInfo;
        _totalProcesses = _processes.Count;

        AddLog("monitor", "info", $"Discovered process: {processInfo.Name} ({GetShortId(processInfo.Id)}) - {processInfo.Status}");

        if (string.IsNullOrEmpty(_selectedProcessId))
        {
          _selectedProcessId = processInfo.Id;
        }
      } else
      {
        if (current.Status != processInfo.Status && !string.IsNullOrEmpty(processInfo.Status))
        {
          AddLog("monitor", "info", $"Process {current.Name} ({GetShortId(current.Id)}) changed status: {current.Status} -> {processInfo.Status}");
        }

        current.Name = processInfo.Name ?? current.Name;
        current.Status = processInfo.Status ?? current.Status;
        current.Mode = processInfo.Mode ?? current.Mode;
        current.StartTime = processInfo.StartTime ?? current.StartTime;
        current.Restarts = processInfo.Restarts ?? current.Restarts;
        current.User = processInfo.User ?? current.User;
      }

      _onlineProcesses = _processes.Values.Count(p => p.Status?.ToLowerInvariant() == "online");
      _lastUpdate = DateTime.UtcNow;

      if ((processInfo.Status?.ToLowerInvariant() == "stopped" ||
           processInfo.Status?.ToLowerInvariant() == "errored") && existing)
      {
        var historyEntry = new GhostProcessInfo
        {
            Id = processInfo.Id,
            Name = processInfo.Name ?? current.Name,
            Status = processInfo.Status,
            Mode = processInfo.Mode ?? current.Mode,
            StartTime = current.StartTime,
            EndTime = DateTime.UtcNow,
            Restarts = current.Restarts,
            User = current.User
        };

        _processHistory.Add(historyEntry);
        _processes.Remove(processInfo.Id);
        _totalProcesses = _processes.Count + _processHistory.Count;

        if (_selectedProcessId == processInfo.Id)
        {
          _selectedProcessId = _processes.Keys.FirstOrDefault();
        }

        AddLog("monitor", "info",
            $"Process {historyEntry.Name} ({GetShortId(historyEntry.Id)}) {historyEntry.Status} " +
            $"after {FormatDuration(historyEntry.EndTime.Value - (historyEntry.StartTime ?? historyEntry.EndTime.Value))}");
      }
    }
  }

  private void UpdateProcessWithMetrics(string processId, ProcessMetrics metrics)
  {
    lock (_lockObject)
    {
      if (_processes.TryGetValue(processId, out var process))
      {
        process.CpuUsage = metrics.CpuPercentage;
        process.MemoryUsage = metrics.MemoryBytes;

        if (process.Status?.ToLowerInvariant() != "online")
        {
          AddLog("monitor", "info", $"Process {process.Name} ({GetShortId(process.Id)}) is now online");
          process.Status = "online";
          _onlineProcesses = _processes.Values.Count(p => p.Status?.ToLowerInvariant() == "online");
        }
      } else
      {
        var newProcess = new GhostProcessInfo
        {
            Id = processId,
            Name = GetDisplayName(processId),
            Status = "online",
            Mode = "app",
            CpuUsage = metrics.CpuPercentage,
            MemoryUsage = metrics.MemoryBytes,
            StartTime = DateTime.UtcNow,
            User = Environment.UserName
        };

        _processes[processId] = newProcess;
        _totalProcesses = _processes.Count;
        _onlineProcesses = _processes.Values.Count(p => p.Status?.ToLowerInvariant() == "online");

        AddLog("monitor", "info", $"Discovered process from metrics: {newProcess.Name} ({GetShortId(newProcess.Id)})");

        if (string.IsNullOrEmpty(_selectedProcessId))
        {
          _selectedProcessId = processId;
        }
      }
    }
  }

  private void ProcessLogMessage(object logData)
  {
    try
    {
      var logEntry = ExtractLogEntry(logData);
      if (logEntry != null)
      {
        if (logEntry.Message?.Contains("Spectre.Console") == true)
          return;

        lock (_lockObject)
        {
          _logs.Add(logEntry);

          if (_logs.Count > 100)
          {
            _logs.RemoveAt(0);
          }
        }
      }
    }
    catch (Exception ex)
    {
      AddLog("monitor", "debug", $"Error extracting log entry: {ex.Message}");
    }
  }

  private LogEntry ExtractLogEntry(object logData)
  {
    try
    {
      if (logData is string stringData)
      {
        return new LogEntry
        {
            Timestamp = DateTime.Now,
            Source = "unknown",
            Level = "info",
            Message = stringData
        };
      }

      try
      {
        var json = JsonSerializer.Serialize(logData);
        var logDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        if (logDict != null)
        {
          var timestamp = DateTime.Now;
          if (logDict.TryGetValue("Timestamp", out var tsElement) &&
              tsElement.ValueKind != JsonValueKind.Undefined &&
              DateTime.TryParse(tsElement.ToString(), out var parsedTs))
          {
            timestamp = parsedTs;
          }

          string source = "unknown";
          if (logDict.TryGetValue("Source", out var srcElement) &&
              srcElement.ValueKind != JsonValueKind.Undefined)
          {
            source = srcElement.ToString();
          }

          string level = "info";
          if (logDict.TryGetValue("Level", out var levelElement) &&
              levelElement.ValueKind != JsonValueKind.Undefined)
          {
            level = levelElement.ToString();
          }

          string message = "";
          if (logDict.TryGetValue("Message", out var msgElement) &&
              msgElement.ValueKind != JsonValueKind.Undefined)
          {
            message = msgElement.ToString();
          }

          return new LogEntry
          {
              Timestamp = timestamp,
              Source = source,
              Level = level,
              Message = message
          };
        }
      }
      catch
      {
        // Continue to fallback
      }

      return new LogEntry
      {
          Timestamp = DateTime.Now,
          Source = "ghost",
          Level = "info",
          Message = logData?.ToString() ?? ""
      };
    }
    catch
    {
      return new LogEntry
      {
          Timestamp = DateTime.Now,
          Source = "unknown",
          Level = "debug",
          Message = "Failed to parse log entry"
      };
    }
  }

  private void AddLog(string source, string level, string message)
  {
    lock (_lockObject)
    {
      _logs.Add(new LogEntry
      {
          Timestamp = DateTime.Now,
          Source = source,
          Level = level,
          Message = message
      });

      if (_logs.Count > 100)
      {
        _logs.RemoveAt(0);
      }
    }
  }

  private string ExtractProcessIdFromTopic(string topic)
  {
    if (topic.StartsWith("ghost:metrics:"))
      return topic.Substring("ghost:metrics:".Length);
    if (topic.StartsWith("ghost:events:"))
      return topic.Substring("ghost:events:".Length);
    return "";
  }

  private async Task<ProcessMetrics> ExtractMetricsAsync(object metricsData, string processId)
  {
    try
    {
      if (metricsData is ProcessMetrics typedMetrics)
        return typedMetrics;

      try
      {
        var serialized = MemoryPackSerializer.Serialize(metricsData);
        return MemoryPackSerializer.Deserialize<ProcessMetrics>(serialized);
      }
      catch
      {
        try
        {
          var json = JsonSerializer.Serialize(metricsData);
          var metricsDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

          if (metricsDict != null)
          {
            return new ProcessMetrics(
                ProcessId: processId,
                CpuPercentage: TryGetDouble(metricsDict, "CpuPercentage"),
                MemoryBytes: TryGetLong(metricsDict, "MemoryBytes"),
                ThreadCount: TryGetInt(metricsDict, "ThreadCount"),
                Timestamp: DateTime.UtcNow,
                HandleCount: TryGetInt(metricsDict, "HandleCount"),
                GcTotalMemory: TryGetLong(metricsDict, "GcTotalMemory"),
                Gen0Collections: TryGetLong(metricsDict, "Gen0Collections"),
                Gen1Collections: TryGetLong(metricsDict, "Gen1Collections"),
                Gen2Collections: TryGetLong(metricsDict, "Gen2Collections")
            );
          }
        }
        catch
        {
          // Continue to last attempt
        }
      }

      return null;
    }
    catch
    {
      return null;
    }
  }

  private double TryGetDouble(Dictionary<string, JsonElement> dict, string key)
  {
    if (dict.TryGetValue(key, out var element) &&
        (element.ValueKind == JsonValueKind.Number || element.ValueKind == JsonValueKind.String))
    {
      if (double.TryParse(element.ToString(), out var result))
        return result;
    }
    return 0.0;
  }

  private long TryGetLong(Dictionary<string, JsonElement> dict, string key)
  {
    if (dict.TryGetValue(key, out var element) &&
        (element.ValueKind == JsonValueKind.Number || element.ValueKind == JsonValueKind.String))
    {
      if (long.TryParse(element.ToString(), out var result))
        return result;
    }
    return 0L;
  }

  private int TryGetInt(Dictionary<string, JsonElement> dict, string key)
  {
    if (dict.TryGetValue(key, out var element) &&
        (element.ValueKind == JsonValueKind.Number || element.ValueKind == JsonValueKind.String))
    {
      if (int.TryParse(element.ToString(), out var result))
        return result;
    }
    return 0;
  }

  private string GetDisplayName(string processId)
  {
    if (string.IsNullOrEmpty(processId)) return "unknown";

    if (processId.StartsWith("app-") && processId.Length > 12)
      return processId.Substring(4, 8);
    if (processId.StartsWith("ghost-"))
      return processId.Substring(6);
    return processId.Length > 12 ? processId.Substring(0, 12) : processId;
  }

  private string GetShortId(string id)
  {
    if (string.IsNullOrEmpty(id)) return "unknown";
    if (id.Length <= 8) return id;
    if (id.StartsWith("app-") && id.Length > 12)
      return id.Substring(4, 8);
    return id.Substring(0, 8);
  }

  private string GetShortName(string name)
  {
    if (string.IsNullOrEmpty(name)) return "unknown";
    return name.Length > 12 ? name.Substring(0, 12) : name;
  }

  private string FormatBytes(long bytes)
  {
    string[] suffix =
    {
        "B", "KB",
        "MB", "GB"
    };
    int i;
    double dblBytes = bytes;
    for(i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
    {
      dblBytes = bytes / 1024.0;
    }
    return $"{dblBytes:0.#}{suffix[i]}";
  }

  private string FormatDuration(TimeSpan duration)
  {
    if (duration.TotalDays >= 1)
      return $"{(int)duration.TotalDays}d {duration.Hours}h";
    if (duration.TotalHours >= 1)
      return $"{(int)duration.TotalHours}h {duration.Minutes}m";
    if (duration.TotalMinutes >= 1)
      return $"{(int)duration.TotalMinutes}m";
    return $"{(int)duration.TotalSeconds}s";
  }
}
public class GhostProcessInfo
{
  public string Id { get; set; } = "";
  public string? Name { get; set; }
  public string? Status { get; set; }
  public string? Mode { get; set; }
  public DateTime? StartTime { get; set; }
  public DateTime? EndTime { get; set; }
  public int? Restarts { get; set; }
  public string? User { get; set; }
  public double? CpuUsage { get; set; }
  public long? MemoryUsage { get; set; }
}
public class LogEntry
{
  public DateTime Timestamp { get; set; }
  public string Source { get; set; } = "";
  public string Level { get; set; } = "";
  public string Message { get; set; } = "";
}
