using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Ghost.Father.CLI.Commands;

public class MonitorCommand : AsyncCommand<MonitorCommand.Settings>
{
    private readonly IGhostBus _bus;
    private readonly Table _systemTable;
    private readonly Table _servicesTable;
    private readonly Table _oneShortAppsTable;
    private readonly Dictionary<string, ProcessState> _processes = new();
    private bool _watching;

    public class Settings : CommandSettings
    {
        [CommandOption("--refresh")]
        [Description("Refresh interval in seconds")]
        [DefaultValue(5)]
        public int RefreshInterval { get; set; }

        [CommandOption("--no-clear")]
        [Description("Don't clear the screen between updates")]
        public bool NoClear { get; set; }

        [CommandOption("--process")]
        [Description("Monitor specific process")]
        public string? ProcessId { get; set; }
    }

    // Process tracking class
    private class ProcessState
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsRunning { get; set; } = true;
        public bool IsService { get; set; }
        public DateTime StartTime { get; set; } = DateTime.UtcNow;
        public DateTime? EndTime { get; set; }
        public ProcessMetrics? LastMetrics { get; set; }
        public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    }

    public MonitorCommand(IGhostBus bus)
    {
        _bus = bus;

        // System status table
        _systemTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[blue]System Status[/]")
            .AddColumn("[grey]Component[/]")
            .AddColumn("[grey]Status[/]")
            .AddColumn("[grey]Info[/]");

        // Services table
        _servicesTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[blue]Services[/]")
            .AddColumn("[grey]Service[/]")
            .AddColumn("[grey]Status[/]")
            .AddColumn("[grey]Started[/]")
            .AddColumn("[grey]Stopped[/]")
            .AddColumn("[grey]Resource Usage[/]")
            .AddColumn("[grey]Actions[/]");

        // One-shot apps table
        _oneShortAppsTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[blue]One-Shot Applications[/]")
            .AddColumn("[grey]App[/]")
            .AddColumn("[grey]Status[/]")
            .AddColumn("[grey]Started[/]")
            .AddColumn("[grey]Completed[/]")
            .AddColumn("[grey]Resource Usage[/]");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            _watching = true;
            if (!settings.NoClear) AnsiConsole.Clear();

            // Create layout
            var layout = new Layout("Root")
                .SplitRows(
                    new Layout("System", _systemTable),
                    new Layout("Services", _servicesTable).Size(10),
                    new Layout("OneShot", _oneShortAppsTable).Size(10));

            // Start live display
            await AnsiConsole.Live(layout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async ctx => {
                    await FetchInitialProcessList(ctx);
                    // Start system status update task
                    Task systemTask = MonitorSystemStatusAsync(ctx, settings);

                    // Start process metrics monitoring task
                    Task metricsTask = MonitorProcessMetricsAsync(ctx, settings);

                    // Start task to check for processes that haven't sent metrics recently
                    Task stalledTask = MonitorStalledProcessesAsync(ctx, settings);

                    // Wait for all tasks
                    await Task.WhenAll(systemTask, metricsTask, stalledTask);
                });

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
        finally
        {
            _watching = false;
        }
    }

    private async Task MonitorSystemStatusAsync(LiveDisplayContext ctx, Settings settings)
    {
        while (_watching)
        {
            try
            {
                UpdateSystemTable();
                ctx.Refresh();
                await Task.Delay(settings.RefreshInterval * 1000);
            }
            catch (Exception ex)
            {
                G.LogError("Error updating system status", ex);
                if (!settings.NoClear) AnsiConsole.Clear();
                AnsiConsole.MarkupLine($"[red]Error updating system status:[/] {ex.Message}");
                await Task.Delay(5000); // Wait before retrying
            }
        }
    }


    private async Task MonitorStalledProcessesAsync(LiveDisplayContext ctx, Settings settings)
    {
        while (_watching)
        {
            try
            {
                // Check for stalled processes (no metrics for more than 10 seconds)
                var now = DateTime.UtcNow;
                var stalledThreshold = TimeSpan.FromSeconds(10);

                bool updated = false;

                foreach (var process in _processes.Values)
                {
                    if (process.IsRunning && now - process.LastSeen > stalledThreshold)
                    {
                        process.IsRunning = false;
                        process.EndTime = process.LastSeen;
                        updated = true;
                    }
                }

                if (updated)
                {
                    UpdateProcessTables();
                    ctx.Refresh();
                }

                await Task.Delay(settings.RefreshInterval * 1000);
            }
            catch (Exception ex)
            {
                G.LogError("Error checking stalled processes", ex);
                await Task.Delay(5000); // Wait before retrying
            }
        }
    }

    private void UpdateSystemTable()
    {
        _systemTable.Rows.Clear();

        // System metrics
        var process = Process.GetCurrentProcess();
        _systemTable.AddRow(
            "CPU",
            $"{process.TotalProcessorTime.TotalSeconds:F1}s",
            $"Threads: {process.Threads.Count}"
        );

        _systemTable.AddRow(
            "Memory",
            FormatBytes(process.WorkingSet64),
            $"Private: {FormatBytes(process.PrivateMemorySize64)}"
        );

        // GC metrics
        _systemTable.AddRow(
            "GC",
            $"Gen0: {GC.CollectionCount(0)}",
            $"Gen1: {GC.CollectionCount(1)}, Gen2: {GC.CollectionCount(2)}"
        );

        // Monitored processes count
        _systemTable.AddRow(
            "Monitoring",
            $"{_processes.Count} processes",
            $"Services: {_processes.Values.Count(p => p.IsService)}, One-shot: {_processes.Values.Count(p => !p.IsService)}"
        );
    }

    private void UpdateProcessState(ProcessMetrics metrics)
    {
        // Determine if this is a service based on process characteristics
        // In a real implementation, this would come from actual process metadata
        bool isService = DetermineIfService(metrics);

        if (!_processes.TryGetValue(metrics.ProcessId, out var process))
        {
            // New process
            process = new ProcessState
            {
                Id = metrics.ProcessId,
                Name = GetDisplayName(metrics.ProcessId),
                IsService = isService,
                StartTime = DateTime.UtcNow,
                LastMetrics = metrics,
                LastSeen = DateTime.UtcNow
            };
            _processes[metrics.ProcessId] = process;
        }
        else
        {
            // Update existing process
            process.LastMetrics = metrics;
            process.LastSeen = DateTime.UtcNow;

            // If previously marked as not running, update it
            if (!process.IsRunning)
            {
                process.IsRunning = true;
                process.EndTime = null;

                // If it previously completed, reset it with a new start time
                if (process.EndTime.HasValue)
                {
                    process.StartTime = DateTime.UtcNow;
                }
            }
        }
    }

    private bool DetermineIfService(ProcessMetrics metrics)
    {
        // In a real implementation, this would use metadata from the process itself
        // Here we use some heuristics to guess
        return metrics.ProcessId.Contains("service", StringComparison.OrdinalIgnoreCase) ||
               metrics.ProcessId.Contains("daemon", StringComparison.OrdinalIgnoreCase) ||
               metrics.ProcessId == "ghost" || // Assume ghost itself is a service
               metrics.ProcessId.EndsWith("d", StringComparison.OrdinalIgnoreCase); // Common daemon suffix
    }

    private void UpdateProcessTables()
    {
        _servicesTable.Rows.Clear();
        _oneShortAppsTable.Rows.Clear();

        // Split processes into services and one-shot apps and sort them
        var services = _processes.Values
            .Where(p => p.IsService)
            .OrderByDescending(p => p.IsRunning)
            .ThenBy(p => p.Name);

        var oneShots = _processes.Values
            .Where(p => !p.IsService)
            .OrderByDescending(p => p.IsRunning)
            .ThenByDescending(p => p.StartTime);

        // Add services to the services table
        foreach (var service in services)
        {
            string statusColor = service.IsRunning ? "green" : "grey";
            string statusText = service.IsRunning ? "Running" : "Stopped";

            _servicesTable.AddRow(
                service.Name,
                $"[{statusColor}]{statusText}[/]",
                FormatDateTime(service.StartTime),
                service.EndTime.HasValue ? FormatDateTime(service.EndTime.Value) : "",
                service.IsRunning && service.LastMetrics != null ?
                    $"CPU: {service.LastMetrics.CpuPercentage:F1}%, Mem: {FormatBytes(service.LastMetrics.MemoryBytes)}" : "",
                service.IsRunning ?
                    $"[blue][[Stop {service.Id}]][/]" :
                    $"[green][[Start {service.Id}]][/]"
            );
        }

        // Add one-shot apps to the one-shot table
        foreach (var app in oneShots)
        {
            string statusColor = app.IsRunning ? "green" : "grey";
            string statusText = app.IsRunning ? "Running" : "Completed";

            _oneShortAppsTable.AddRow(
                app.IsRunning ? $"[bold]{app.Name}[/]" : app.Name,
                $"[{statusColor}]{statusText}[/]",
                FormatDateTime(app.StartTime),
                app.EndTime.HasValue ? FormatDateTime(app.EndTime.Value) : "",
                app.IsRunning && app.LastMetrics != null ?
                    $"CPU: {app.LastMetrics.CpuPercentage:F1}%, Mem: {FormatBytes(app.LastMetrics.MemoryBytes)}" : ""
            );
        }
    }

    private string GetDisplayName(string processId)
    {
        // Try to extract a friendly name from the process ID
        // This is a simplified version and would be more robust in a real implementation

        // If it starts with "ghost:", extract the name after the colon
        if (processId.StartsWith("ghost:"))
        {
            return processId.Substring(6);
        }

        // Remove any GUID-like suffixes
        if (processId.Length > 36 && processId[^36] == '-' &&
            Guid.TryParse(processId.Substring(processId.Length - 36), out _))
        {
            return processId.Substring(0, processId.Length - 37);
        }

        return processId;
    }

    private string FormatDateTime(DateTime dt)
    {
        // If it's today, just show the time
        if (dt.Date == DateTime.Today)
        {
            return dt.ToString("HH:mm:ss");
        }

        // Otherwise show date and time
        return dt.ToString("yyyy-MM-dd HH:mm:ss");
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblBytes = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblBytes = bytes / 1024.0;
        }
        return $"{dblBytes:0.##}{suffix[i]}";
    }

    private async Task FetchInitialProcessListAsync(LiveDisplayContext ctx, Settings settings)
    {
        try {
            // Query processes directly from the daemon
            var command = new SystemCommand {
                    CommandId = Guid.NewGuid().ToString(),
                    CommandType = "status",
                    Parameters = new Dictionary<string, string>()
            };

            await _bus.PublishAsync("ghost:commands", command);

            // Wait for response
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var response in _bus.SubscribeAsync<CommandResponse>("ghost:responses", cts.Token)) {
                if (response.CommandId == command.CommandId && response.Data != null) {
                    // Process the response data and update tables
                    //UpdateProcessesFromStatusResponse(response.Data);
                    ctx.Refresh();
                    break;
                }
            }
        } catch (Exception ex) {
            G.LogError(ex, "Failed to fetch initial process list");
        }
    }

    private async Task SendControlCommandAsync(string processId, string command)
    {
        try
        {
            // Create a system command to control the process
            var systemCommand = new SystemCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = command,
                Parameters = new Dictionary<string, string>
                {
                    ["responseChannel"] = "ghost:monitor:response"
                }
            };

            // Send the command
            await _bus.PublishAsync("ghost:commands", systemCommand);

            // Log the action
            G.LogInfo($"Sent {command} command to process {processId}");
        }
        catch (Exception ex)
        {
            G.LogError(ex, $"Failed to send {command} command to process {processId}");
        }
    }

    private async Task MonitorProcessMetricsAsync(LiveDisplayContext ctx, Settings settings)
    {
        try
        {
            // Fixed topic pattern using * instead of # for wildcard matching
            var filter = settings.ProcessId != null ?
                $"ghost:metrics:{settings.ProcessId}" :
                "ghost:metrics:*";

            await foreach (var metrics in _bus.SubscribeAsync<dynamic>(filter))
            {
                if (!_watching) break;

                try
                {
                    // Extract processId from topic pattern
                    var topic = _bus.GetLastTopic();
                    var processId = topic.Substring("ghost:metrics:".Length);

                    // Update process state with metrics
                    if (!_processes.TryGetValue(processId, out var process))
                    {
                        // New process discovered through metrics
                        process = new ProcessState
                        {
                            Id = processId,
                            Name = GetDisplayName(processId),
                            IsRunning = true,
                            StartTime = DateTime.UtcNow,
                            LastSeen = DateTime.UtcNow
                        };

                        // Determine if service based on metadata
                        // Check for AppType tag
                        if (metrics.GetType().GetProperty("Tags") != null)
                        {
                            var tags = metrics.Tags as Dictionary<string, string>;
                            if (tags != null && tags.TryGetValue("AppType", out var appType))
                            {
                                process.IsService = string.Equals(appType, "service", StringComparison.OrdinalIgnoreCase);
                            }
                        }

                        _processes[processId] = process;
                    }

                    // Update metrics
                    process.LastMetrics = metrics;
                    process.LastSeen = DateTime.UtcNow;

                    // Update display
                    UpdateProcessTables();
                    ctx.Refresh();
                }
                catch (Exception ex)
                {
                    G.LogError(ex, "Error processing metrics");
                }
            }
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error monitoring process metrics");
        }
    }

    // Updated method to fetch initial process list
    private async Task FetchInitialProcessList(LiveDisplayContext ctx)
    {
        try
        {
            // Query GhostFather for all processes
            var command = new SystemCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = "status",
                Parameters = new Dictionary<string, string>()
            };

            await _bus.PublishAsync("ghost:commands", command);

            // Wait for response with timeout
            var responseReceived = new TaskCompletionSource<bool>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await foreach (var response in _bus.SubscribeAsync<CommandResponse>("ghost:responses", cts.Token))
                {
                    if (response.CommandId == command.CommandId && response.Success)
                    {
                        if (response.Data != null)
                        {
                            try
                            {
                                // Process the data
                                UpdateProcessesFromStatusResponse(response.Data);

                                // Update tables
                                UpdateProcessTables();
                                ctx.Refresh();
                            }
                            catch (Exception ex)
                            {
                                G.LogError(ex, "Error processing status response data");
                            }
                        }

                        responseReceived.TrySetResult(true);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout - continue without initial data
                G.LogWarn("Timeout waiting for process list");
                responseReceived.TrySetResult(false);
            }

            await responseReceived.Task;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error fetching initial process list");
        }
    }

    // Helper method to update process data from status response
    private void UpdateProcessesFromStatusResponse(object data)
    {
        if (data == null) return;

        try
        {
            // Convert data to dictionary
            var dataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                JsonSerializer.Serialize(data));

            if (dataDict != null && dataDict.TryGetValue("Processes", out var processesObj))
            {
                // Parse processes array
                var processes = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(
                    JsonSerializer.Serialize(processesObj));

                if (processes != null)
                {
                    foreach (var processDict in processes)
                    {
                        if (processDict.TryGetValue("id", out var idObj) &&
                            idObj != null &&
                            processDict.TryGetValue("status", out var statusObj) &&
                            statusObj != null)
                        {
                            var id = idObj.ToString();
                            var status = statusObj.ToString();

                            // Get or create process state
                            if (!_processes.TryGetValue(id, out var process))
                            {
                                process = new ProcessState
                                {
                                    Id = id,
                                    Name = processDict.TryGetValue("name", out var nameObj) && nameObj != null ?
                                        nameObj.ToString() : GetDisplayName(id)
                                };

                                // Set start time if available
                                if (processDict.TryGetValue("StartTime", out var startTimeObj) &&
                                    startTimeObj != null &&
                                    DateTime.TryParse(startTimeObj.ToString(), out var startTime))
                                {
                                    process.StartTime = startTime;
                                }
                                else
                                {
                                    process.StartTime = DateTime.UtcNow;
                                }

                                _processes[id] = process;
                            }

                            // Update status
                            process.IsRunning = status.Equals("Running", StringComparison.OrdinalIgnoreCase);

                            // Update end time if stopped
                            if (!process.IsRunning && !process.EndTime.HasValue &&
                                processDict.TryGetValue("LastUpdate", out var lastUpdateObj) &&
                                lastUpdateObj != null &&
                                DateTime.TryParse(lastUpdateObj.ToString(), out var lastUpdate))
                            {
                                process.EndTime = lastUpdate;
                            }

                            // Determine if service
                            if (processDict.TryGetValue("type", out var typeObj) && typeObj != null)
                            {
                                var type = typeObj.ToString();
                                process.IsService = type.Equals("service", StringComparison.OrdinalIgnoreCase) ||
                                                   type.Equals("daemon", StringComparison.OrdinalIgnoreCase);
                            }

                            // Set last seen time
                            process.LastSeen = DateTime.UtcNow;

                            // Update metrics if available
                            if (processDict.TryGetValue("CpuPercentage", out var cpuObj) &&
                                processDict.TryGetValue("MemoryBytes", out var memObj))
                            {
                                // Create basic metrics object
                                var metrics = new Dictionary<string, object>();

                                if (cpuObj != null)
                                {
                                    metrics["CpuPercentage"] = Convert.ToDouble(cpuObj);
                                }

                                if (memObj != null)
                                {
                                    metrics["MemoryBytes"] = Convert.ToInt64(memObj);
                                }

                                // Set as dynamic object
                                process.LastMetrics = JsonSerializer.Deserialize<dynamic>(
                                    JsonSerializer.Serialize(metrics));
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error parsing status response");
        }
    }

}