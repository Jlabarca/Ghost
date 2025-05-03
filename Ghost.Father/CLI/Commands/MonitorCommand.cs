using Ghost.Core;
using Ghost.Core.Storage;
using MemoryPack;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class MonitorCommand : AsyncCommand<MonitorCommand.Settings>
{
    private readonly IGhostBus _bus;
    private readonly Table _systemTable;
    private readonly Table _servicesTable;
    private readonly Table _oneShotAppsTable;
    private readonly Dictionary<string, ProcessState> _processes = new();
    private readonly ConcurrentDictionary<string, ProcessMetrics> _latestMetrics = new();
    private bool _watching;
    private string _lastError = string.Empty;
    private DateTime _lastRefresh = DateTime.MinValue;
    private CancellationTokenSource _monitoringCts = new();

    public class Settings : CommandSettings
    {
        [CommandOption("--refresh")]
        [Description("Refresh interval in seconds")]
        [DefaultValue(3)]
        public int RefreshInterval { get; set; }

        [CommandOption("--no-clear")]
        [Description("Don't clear the screen between updates")]
        public bool NoClear { get; set; }

        [CommandOption("--process")]
        [Description("Monitor specific process")]
        public string? ProcessId { get; set; }

        [CommandOption("--auto-start")]
        [Description("Automatically start the daemon if not running")]
        public bool AutoStart { get; set; }
    }

    public MonitorCommand(IGhostBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));

        // System status table
        _systemTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[blue]System Status[/]")
            .AddColumn(new TableColumn("[grey]Component[/]").Width(15))
            .AddColumn(new TableColumn("[grey]Status[/]").Width(15))
            .AddColumn(new TableColumn("[grey]Info[/]"));

        // Services table
        _servicesTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[blue]Services[/]")
            .AddColumn(new TableColumn("[grey]Service[/]").Width(20))
            .AddColumn(new TableColumn("[grey]Status[/]").Width(12))
            .AddColumn(new TableColumn("[grey]Started[/]").Width(12))
            .AddColumn(new TableColumn("[grey]Uptime[/]").Width(12))
            .AddColumn(new TableColumn("[grey]Resource Usage[/]").Width(25))
            .AddColumn(new TableColumn("[grey]Actions[/]").Width(15));

        // One-shot apps table
        _oneShotAppsTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[blue]One-Shot Applications[/]")
            .AddColumn(new TableColumn("[grey]App[/]").Width(20))
            .AddColumn(new TableColumn("[grey]Status[/]").Width(12))
            .AddColumn(new TableColumn("[grey]Started[/]").Width(12))
            .AddColumn(new TableColumn("[grey]Completed[/]").Width(12))
            .AddColumn(new TableColumn("[grey]Resource Usage[/]").Width(25));
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            _watching = true;
            if (!settings.NoClear) AnsiConsole.Clear();

            // Check connection to the daemon
            if (!await ConnectToDaemonAsync())
            {
                if (settings.AutoStart)
                {
                    AnsiConsole.MarkupLine("[yellow]GhostFather daemon not running. Attempting to start it...[/]");
                    if (!await StartDaemonAsync())
                    {
                        AnsiConsole.MarkupLine("[red]Error:[/] Failed to start GhostFather daemon.");
                        return 1;
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] Cannot connect to GhostFather daemon.");
                    return 1;
                }
            }

            // Create layout
            var layout = new Layout("Root")
                .SplitRows(
                    new Layout("System", _systemTable),
                    new Layout("Services", _servicesTable).Size(10),
                    new Layout("OneShot", _oneShotAppsTable).Size(10));

            // Add help text
            AnsiConsole.MarkupLine("[grey]Press [blue]R[/] to refresh, [blue]S[/] to stop a process, [blue]A[/] to start a process, [blue]Q[/] to quit[/]");

            // Start live display
            await AnsiConsole.Live(layout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async ctx =>
                {
                    // Start all monitoring tasks
                    var tasks = new List<Task>();

                    // First, fetch initial processes to populate the UI
                    await FetchInitialProcessList(ctx);

                    // Reset cancellation token for monitoring
                    _monitoringCts = new CancellationTokenSource();

                    // Start monitoring tasks
                    tasks.Add(MonitorSystemStatusAsync(ctx, settings));
                    tasks.Add(MonitorProcessMetricsAsync(ctx, settings));
                    tasks.Add(MonitorStateUpdatesAsync(ctx, settings));
                    tasks.Add(HandleUserInputAsync(ctx));

                    // Wait for either all tasks to complete or user to quit
                    await Task.WhenAny(tasks);

                    // Cancel other tasks
                    _monitoringCts.Cancel();
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
            _monitoringCts.Cancel();
            _monitoringCts.Dispose();
        }
    }

    private async Task<bool> StartDaemonAsync()
    {
        try
        {
            AnsiConsole.MarkupLine("[grey]Attempting to start GhostFather daemon...[/]");

            // This is a simplified approach - in a real implementation, you'd need a proper
            // way to find and start the GhostFather daemon process
            var processInfo = new ProcessStartInfo
            {
                FileName = "ghost-father-daemon",
                UseShellExecute = true,
                CreateNoWindow = false
            };

            Process.Start(processInfo);

            // Wait for daemon to start
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                if (await ConnectToDaemonAsync())
                {
                    AnsiConsole.MarkupLine("[green]GhostFather daemon started successfully.[/]");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error starting daemon:[/] {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ConnectToDaemonAsync()
    {
        try
        {
            // Send a ping command to check if daemon is running
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
            await _bus.PublishAsync("ghost:commands", pingCommand);

            // Wait for response with timeout
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
                {
                    if (response.CommandId == pingCommand.CommandId && response.Success)
                    {
                        return true;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout occurred
                return false;
            }

            return false;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            return false;
        }
    }

    private async Task MonitorSystemStatusAsync(LiveDisplayContext ctx, Settings settings)
    {
        while (_watching && !_monitoringCts.IsCancellationRequested)
        {
            try
            {
                UpdateSystemTable();
                ctx.Refresh();
                await Task.Delay(settings.RefreshInterval * 1000, _monitoringCts.Token);
            }
            catch (TaskCanceledException)
            {
                // Normal cancellation
                break;
            }
            catch (Exception ex)
            {
                _lastError = $"Error updating system status: {ex.Message}";
                await Task.Delay(1000, _monitoringCts.Token); // Shorter delay on error
            }
        }
    }

    private async Task MonitorStateUpdatesAsync(LiveDisplayContext ctx, Settings settings)
    {
        while (_watching && !_monitoringCts.IsCancellationRequested)
        {
            try
            {
                // Only refresh if we haven't refreshed recently
                if ((DateTime.UtcNow - _lastRefresh).TotalSeconds >= settings.RefreshInterval)
                {
                    await FetchProcessStatusUpdates(ctx);
                    _lastRefresh = DateTime.UtcNow;
                }

                UpdateProcessTables();
                ctx.Refresh();

                await Task.Delay(settings.RefreshInterval * 1000, _monitoringCts.Token);
            }
            catch (TaskCanceledException)
            {
                // Normal cancellation
                break;
            }
            catch (Exception ex)
            {
                _lastError = $"Error monitoring state updates: {ex.Message}";
                await Task.Delay(1000, _monitoringCts.Token); // Shorter delay on error
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

        // Process statistics
        _systemTable.AddRow(
            "Monitoring",
            $"{_processes.Count} processes",
            $"Services: {_processes.Values.Count(p => p.IsService)}, One-shot: {_processes.Values.Count(p => !p.IsService)}"
        );

        // Daemon status
        _systemTable.AddRow(
            "Daemon",
            _processes.TryGetValue("ghost-daemon", out var daemon) && daemon.IsRunning ?
                "[green]Connected[/]" : "[yellow]Disconnected[/]",
            _lastError.Length > 0 ? $"[yellow]{_lastError}[/]" : "Running normally"
        );

        // Last refresh time
        _systemTable.AddRow(
            "Last Update",
            _lastRefresh == DateTime.MinValue ?
                "[yellow]Pending[/]" : $"[grey]{FormatTimeAgo(_lastRefresh)}[/]",
            ""
        );
    }

    private async Task FetchInitialProcessList(LiveDisplayContext ctx)
    {
        try
        {
            // Query GhostFather for all processes
            var command = new SystemCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = "status",
                Parameters = new Dictionary<string, string>
                {
                    ["responseChannel"] = $"ghost:responses:{Guid.NewGuid()}"
                }
            };

            var responseChannel = command.Parameters["responseChannel"];
            await _bus.PublishAsync("ghost:commands", command);

            AnsiConsole.MarkupLine("[grey]Requesting process list from daemon...[/]");

            // Wait for response with timeout
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
                {
                    if (response.CommandId == command.CommandId)
                    {
                        if (response.Success && response.Data != null)
                        {
                            try
                            {
                                // Process the response data using MemoryPack serialization
                                if (response.Data is ProcessListResponse processListResponse &&
                                    processListResponse.Processes != null)
                                {
                                    // Direct access to process list
                                    foreach (var process in processListResponse.Processes)
                                    {
                                        _processes[process.Id] = new ProcessState
                                        {
                                            Id = process.Id,
                                            Name = process.Name ?? GetDisplayName(process.Id),
                                            IsService = process.IsService,//DetermineIfService(process.LastMetrics),
                                            IsRunning = process.Status == ProcessStatus.Running,
                                            StartTime = process.StartTime,
                                            EndTime = process.Status != ProcessStatus.Running ? DateTime.UtcNow : null,
                                            LastSeen = DateTime.UtcNow
                                        };
                                    }

                                    AnsiConsole.MarkupLine($"[grey]Received {processListResponse.Processes.Count} processes from daemon[/]");
                                }
                                else
                                {
                                    // If we didn't get a standard ProcessListResponse, attempt to extract process info
                                    await ExtractProcessesFromResponseData(response.Data);
                                }

                                // Update tables
                                UpdateProcessTables();
                                ctx.Refresh();
                                _lastRefresh = DateTime.UtcNow;
                            }
                            catch (Exception ex)
                            {
                                _lastError = $"Error processing status response: {ex.Message}";
                                AnsiConsole.MarkupLine($"[yellow]Error processing response: {ex.Message}[/]");
                            }
                        }
                        else if (!response.Success)
                        {
                            _lastError = response.Error ?? "Unknown error";
                            AnsiConsole.MarkupLine($"[yellow]Daemon returned error: {response.Error}[/]");
                        }

                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _lastError = "Timeout waiting for process list";
                AnsiConsole.MarkupLine("[yellow]Timeout waiting for process list from daemon[/]");
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            AnsiConsole.MarkupLine($"[red]Error fetching initial process list: {ex.Message}[/]");
        }
    }

    private async Task FetchProcessStatusUpdates(LiveDisplayContext ctx)
    {
        try
        {
            // Query GhostFather for all processes
            var command = new SystemCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = "status",
                Parameters = new Dictionary<string, string>
                {
                    ["responseChannel"] = $"ghost:responses:{Guid.NewGuid()}"
                }
            };

            var responseChannel = command.Parameters["responseChannel"];
            await _bus.PublishAsync("ghost:commands", command);

            // Wait for response with timeout
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
                {
                    if (response.CommandId == command.CommandId)
                    {
                        if (response.Success && response.Data != null)
                        {
                            await ExtractProcessesFromResponseData(response.Data);
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timeout - we'll try again next cycle
            }
        }
        catch (Exception ex)
        {
            _lastError = $"Status update error: {ex.Message}";
        }
    }

    private async Task ExtractProcessesFromResponseData(object responseData)
    {
        try
        {
            // Try to extract process list using different methods
            if (responseData is ProcessListResponse processListResponse &&
                processListResponse.Processes != null)
            {
                // Direct ProcessListResponse
                foreach (var process in processListResponse.Processes)
                {
                    UpdateProcessState(process);
                }
                return;
            }

            // Attempt to serialize the response data to bytes and then deserialize as different types
            byte[] serialized = MemoryPackSerializer.Serialize(responseData);

            try
            {
                // Try to deserialize as a dictionary
                var dataDict = MemoryPackSerializer.Deserialize<Dictionary<string, object>>(serialized);

                if (dataDict != null && dataDict.TryGetValue("Processes", out var processesObj))
                {
                    byte[] processesSerialized = MemoryPackSerializer.Serialize(processesObj);
                    var processList = MemoryPackSerializer.Deserialize<List<ProcessState>>(processesSerialized);

                    if (processList != null)
                    {
                        foreach (var process in processList)
                        {
                            _processes[process.Id] = process;
                        }
                        return;
                    }

                    // Alternative: try as dictionary array
                    var processesDict = MemoryPackSerializer.Deserialize<List<Dictionary<string, object>>>(processesSerialized);
                    if (processesDict != null)
                    {
                        foreach (var processDict in processesDict)
                        {
                            if (processDict.TryGetValue("id", out var idObj) && idObj != null)
                            {
                                var id = idObj.ToString();

                                // Get or create process state
                                if (!_processes.TryGetValue(id, out var process))
                                {
                                    process = new ProcessState
                                    {
                                        Id = id,
                                        Name = processDict.TryGetValue("name", out var nameObj) && nameObj != null ?
                                            nameObj.ToString() : GetDisplayName(id)
                                    };
                                    _processes[id] = process;
                                }

                                // Update status
                                if (processDict.TryGetValue("status", out var statusObj) && statusObj != null)
                                {
                                    var statusStr = statusObj.ToString();
                                    process.IsRunning = statusStr.Equals("Running", StringComparison.OrdinalIgnoreCase);
                                }

                                // Update timestamps
                                if (processDict.TryGetValue("StartTime", out var startTimeObj) && startTimeObj != null &&
                                    DateTime.TryParse(startTimeObj.ToString(), out var startTime))
                                {
                                    process.StartTime = startTime;
                                }

                                if (!process.IsRunning && processDict.TryGetValue("LastUpdate", out var lastUpdateObj) &&
                                    lastUpdateObj != null && DateTime.TryParse(lastUpdateObj.ToString(), out var lastUpdate))
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

                                process.LastSeen = DateTime.UtcNow;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = $"Failed to parse process data: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            _lastError = $"Data extraction error: {ex.Message}";
        }
    }

    private void UpdateProcessState(ProcessState process)
    {
        if (process == null || string.IsNullOrEmpty(process.Id)) return;

        if (!_processes.TryGetValue(process.Id, out var existingProcess))
        {
            // New process
            _processes[process.Id] = process;
            process.LastSeen = DateTime.UtcNow;
        }
        else
        {
            // Update existing process
            existingProcess.IsRunning = process.IsRunning;
            existingProcess.Name = process.Name ?? existingProcess.Name;
            existingProcess.IsService = process.IsService;

            if (process.StartTime != default)
            {
                existingProcess.StartTime = process.StartTime;
            }

            if (!process.IsRunning && process.EndTime.HasValue)
            {
                existingProcess.EndTime = process.EndTime;
            }
            else if (!process.IsRunning && !existingProcess.EndTime.HasValue)
            {
                existingProcess.EndTime = DateTime.UtcNow;
            }
            else if (process.IsRunning)
            {
                existingProcess.EndTime = null;
            }

            existingProcess.LastSeen = DateTime.UtcNow;
        }
    }

    private async Task MonitorProcessMetricsAsync(LiveDisplayContext ctx, Settings settings)
    {
        try
        {
            // Determine metrics channel based on settings
            var filter = settings.ProcessId != null
                ? $"ghost:metrics:{settings.ProcessId}"
                : "ghost:metrics:*";

            // Subscribe to metrics updates
            await foreach (var metricsData in _bus.SubscribeAsync<object>(filter, _monitoringCts.Token))
            {
                if (!_watching || _monitoringCts.IsCancellationRequested) break;

                try
                {
                    // Extract processId from topic pattern
                    var topic = _bus.GetLastTopic();
                    var processId = topic.Substring("ghost:metrics:".Length);

                    // Handle different metrics formats
                    ProcessMetrics metrics = await ExtractMetricsFromData(metricsData, processId);

                    if (metrics != null)
                    {
                        // Store latest metrics
                        _latestMetrics[processId] = metrics;

                        // Update process state if we have this process
                        if (_processes.TryGetValue(processId, out var process))
                        {
                            process.LastMetrics = metrics;
                            process.IsRunning = true; // If we're getting metrics, process is running
                            process.LastSeen = DateTime.UtcNow;

                            // If it previously was marked as not running, reset end time
                            if (process.EndTime.HasValue)
                            {
                                process.EndTime = null;
                            }
                        }
                        else
                        {
                            // We got metrics for a process we don't know about yet
                            // Create a minimal process state
                            _processes[processId] = new ProcessState
                            {
                                Id = processId,
                                Name = GetDisplayName(processId),
                                IsService = DetermineIfService(metrics),
                                IsRunning = true,
                                StartTime = DateTime.UtcNow,
                                LastSeen = DateTime.UtcNow,
                                LastMetrics = metrics
                            };
                        }

                        // Update display
                        UpdateProcessTables();
                        ctx.Refresh();
                    }
                }
                catch (Exception ex)
                {
                    _lastError = $"Metrics error: {ex.Message}";
                }
            }
        }
        catch (TaskCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            _lastError = $"Metrics subscription error: {ex.Message}";
        }
    }

    private async Task<ProcessMetrics> ExtractMetricsFromData(object metricsData, string processId)
    {
        try
        {
            // If it's already the right type, use it directly
            if (metricsData is ProcessMetrics typedMetrics)
            {
                return typedMetrics;
            }

            // Try to convert using MemoryPack
            byte[] serialized = MemoryPackSerializer.Serialize(metricsData);

            try
            {
                var metrics = MemoryPackSerializer.Deserialize<ProcessMetrics>(serialized);
                if (metrics != null)
                {
                    return metrics;
                }
            }
            catch { /* Continue with other methods */ }

            // Try to extract as properties dictionary
            try
            {
                var propDict = MemoryPackSerializer.Deserialize<Dictionary<string, object>>(serialized);
                if (propDict != null)
                {
                    return new ProcessMetrics(
                        ProcessId: processId,
                        CpuPercentage: TryGetValue<double>(propDict, "CpuPercentage", 0),
                        MemoryBytes: TryGetValue<long>(propDict, "MemoryBytes", 0),
                        ThreadCount: TryGetValue<int>(propDict, "ThreadCount", 0),
                        Timestamp: TryGetValue<DateTime>(propDict, "Timestamp", DateTime.UtcNow),
                        HandleCount: TryGetValue<int>(propDict, "HandleCount", 0),
                        GcTotalMemory: TryGetValue<long>(propDict, "GcTotalMemory", 0),
                        Gen0Collections: TryGetValue<long>(propDict, "Gen0Collections", 0),
                        Gen1Collections: TryGetValue<long>(propDict, "Gen1Collections", 0),
                        Gen2Collections: TryGetValue<long>(propDict, "Gen2Collections", 0)
                    );
                }
            }
            catch { /* Continue with other methods */ }

            // Last resort: use reflection to extract properties
            return ExtractMetricsWithReflection(metricsData, processId);
        }
        catch (Exception ex)
        {
            _lastError = $"Error extracting metrics: {ex.Message}";
            return null;
        }
    }

    private ProcessMetrics ExtractMetricsWithReflection(object metricsData, string processId)
    {
        if (metricsData == null) return null;

        var type = metricsData.GetType();

        double cpuPercentage = 0;
        long memoryBytes = 0;
        int threadCount = 0;
        int handleCount = 0;
        long gcTotalMemory = 0;
        long gen0Collections = 0;
        long gen1Collections = 0;
        long gen2Collections = 0;

        // Try to read properties using reflection
        var cpuProp = type.GetProperty("CpuPercentage") ?? type.GetProperty("CpuUsage");
        if (cpuProp != null)
        {
            var value = cpuProp.GetValue(metricsData);
            if (value != null) double.TryParse(value.ToString(), out cpuPercentage);
        }

        var memProp = type.GetProperty("MemoryBytes") ?? type.GetProperty("Memory") ?? type.GetProperty("WorkingSet64");
        if (memProp != null)
        {
            var value = memProp.GetValue(metricsData);
            if (value != null) long.TryParse(value.ToString(), out memoryBytes);
        }

        var threadProp = type.GetProperty("ThreadCount") ?? type.GetProperty("Threads");
        if (threadProp != null)
        {
            var value = threadProp.GetValue(metricsData);
            if (value != null) int.TryParse(value.ToString(), out threadCount);
        }

        var handleProp = type.GetProperty("HandleCount") ?? type.GetProperty("Handles");
        if (handleProp != null)
        {
            var value = handleProp.GetValue(metricsData);
            if (value != null) int.TryParse(value.ToString(), out handleCount);
        }

        var gcProp = type.GetProperty("GcTotalMemory");
        if (gcProp != null)
        {
            var value = gcProp.GetValue(metricsData);
            if (value != null) long.TryParse(value.ToString(), out gcTotalMemory);
        }

        var gen0Prop = type.GetProperty("Gen0Collections");
        if (gen0Prop != null)
        {
            var value = gen0Prop.GetValue(metricsData);
            if (value != null) long.TryParse(value.ToString(), out gen0Collections);
        }

        var gen1Prop = type.GetProperty("Gen1Collections");
        if (gen1Prop != null)
        {
            var value = gen1Prop.GetValue(metricsData);
            if (value != null) long.TryParse(value.ToString(), out gen1Collections);
        }

        var gen2Prop = type.GetProperty("Gen2Collections");
        if (gen2Prop != null)
        {
            var value = gen2Prop.GetValue(metricsData);
            if (value != null) long.TryParse(value.ToString(), out gen2Collections);
        }

        return new ProcessMetrics(
            ProcessId: processId,
            CpuPercentage: cpuPercentage,
            MemoryBytes: memoryBytes,
            ThreadCount: threadCount,
            Timestamp: DateTime.UtcNow,
            HandleCount: handleCount,
            GcTotalMemory: gcTotalMemory,
            Gen0Collections: gen0Collections,
            Gen1Collections: gen1Collections,
            Gen2Collections: gen2Collections
        );
    }

    private T TryGetValue<T>(Dictionary<string, object> dict, string key, T defaultValue)
    {
        if (dict == null || !dict.TryGetValue(key, out var value) || value == null)
            return defaultValue;

        if (value is T typedValue)
            return typedValue;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    private void UpdateProcessTables()
    {
        _servicesTable.Rows.Clear();
        _oneShotAppsTable.Rows.Clear();

        // Split processes into services and one-shot apps and sort them
        var services = _processes.Values
            .Where(p => p.IsService)
            .OrderByDescending(p => p.IsRunning)
            .ThenBy(p => p.Name)
            .ToList();

        var oneShots = _processes.Values
            .Where(p => !p.IsService)
            .OrderByDescending(p => p.IsRunning)
            .ThenByDescending(p => p.StartTime)
            .ToList();

        // Check for stalled processes
        var now = DateTime.UtcNow;
        var stalledThreshold = TimeSpan.FromSeconds(15);

        foreach (var process in _processes.Values)
        {
            if (process.IsRunning && now - process.LastSeen > stalledThreshold)
            {
                process.IsRunning = false;
                process.EndTime = process.LastSeen;
            }
        }

        // Add services to the services table
        foreach (var service in services)
        {
            string statusColor = service.IsRunning ? "green" : "grey";
            string statusText = service.IsRunning ? "Running" : "Stopped";

            var resourceUsage = GetResourceUsageText(service);
            var uptime = service.IsRunning && service.StartTime != default
                ? FormatTimeSpan(DateTime.UtcNow - service.StartTime)
                : "";

            _servicesTable.AddRow(
                service.Name,
                $"[{statusColor}]{statusText}[/]",
                FormatDateTime(service.StartTime),
                uptime,
                resourceUsage,
                service.IsRunning ? $"[blue][[Stop]][/]" : $"[green][[Start]][/]"
            );
        }

        // Add one-shot apps to the one-shot table
        foreach (var app in oneShots)
        {
            string statusColor = app.IsRunning ? "green" : "grey";
            string statusText = app.IsRunning ? "Running" : "Completed";

            var resourceUsage = GetResourceUsageText(app);

            _oneShotAppsTable.AddRow(
                app.IsRunning ? $"[bold]{app.Name}[/]" : app.Name,
                $"[{statusColor}]{statusText}[/]",
                FormatDateTime(app.StartTime),
                app.EndTime.HasValue ? FormatDateTime(app.EndTime.Value) : "",
                resourceUsage
            );
        }
    }

    private string GetResourceUsageText(ProcessState process)
    {
        if (!process.IsRunning || process.LastMetrics == null)
            return "";

        return $"CPU: {process.LastMetrics.CpuPercentage:F1}%, Mem: {FormatBytes(process.LastMetrics.MemoryBytes)}";
    }

    private bool DetermineIfService(ProcessState process)
    {
        return process.Id.Contains("service", StringComparison.OrdinalIgnoreCase) ||
               process.Id.Contains("daemon", StringComparison.OrdinalIgnoreCase) ||
               process.Id == "ghost-daemon" ||
               process.Id.EndsWith("d", StringComparison.OrdinalIgnoreCase);
    }

    private bool DetermineIfService(ProcessMetrics metrics)
    {
        // In a real implementation, this would use metadata from the process itself
        return metrics.ProcessId.Contains("service", StringComparison.OrdinalIgnoreCase) ||
               metrics.ProcessId.Contains("daemon", StringComparison.OrdinalIgnoreCase) ||
               metrics.ProcessId == "ghost-daemon" ||
               metrics.ProcessId.EndsWith("d", StringComparison.OrdinalIgnoreCase);
    }

    private string GetDisplayName(string processId)
    {
        // Try to extract a friendly name from the process ID
        if (processId.StartsWith("ghost:"))
        {
            return processId.Substring(6);
        }

        // Remove any GUID-like suffixes
        if (processId.Length > 36 && processId[^36] == '-' && Guid.TryParse(processId.Substring(processId.Length - 36), out _))
        {
            return processId.Substring(0, processId.Length - 37);
        }

        return processId;
    }

    private string FormatDateTime(DateTime dt)
    {
        if (dt == default) return "";

        // Convert to local time
        var localDt = dt.ToLocalTime();

        // If it's today, just show the time
        if (localDt.Date == DateTime.Today)
        {
            return localDt.ToString("HH:mm:ss");
        }

        // If it's this year, show date without year
        if (localDt.Year == DateTime.Today.Year)
        {
            return localDt.ToString("MMM dd HH:mm");
        }

        // Otherwise show date with year
        return localDt.ToString("yyyy-MM-dd HH:mm");
    }

    private string FormatTimeAgo(DateTime dt)
    {
        var span = DateTime.UtcNow - dt;

        if (span.TotalSeconds < 60)
            return $"{span.TotalSeconds:F0}s ago";

        if (span.TotalMinutes < 60)
            return $"{span.TotalMinutes:F0}m ago";

        if (span.TotalHours < 24)
            return $"{span.TotalHours:F0}h ago";

        return $"{span.TotalDays:F0}d ago";
    }

    private string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{span.TotalDays:F1}d";

        if (span.TotalHours >= 1)
            return $"{span.TotalHours:F1}h";

        if (span.TotalMinutes >= 1)
            return $"{span.TotalMinutes:F1}m";

        return $"{span.TotalSeconds:F0}s";
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

    private async Task HandleUserInputAsync(LiveDisplayContext ctx)
    {
        while (_watching && !_monitoringCts.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);

                switch (key.Key)
                {
                    case ConsoleKey.R:
                        // Refresh
                        AnsiConsole.MarkupLine("[grey]Refreshing process list...[/]");
                        await FetchInitialProcessList(ctx);
                        break;

                    case ConsoleKey.S:
                        // Stop a process
                        var runningProcesses = _processes.Values
                            .Where(p => p.IsRunning)
                            .Select(p => $"{p.Id}: {p.Name}")
                            .ToList();

                        if (runningProcesses.Count > 0)
                        {
                            var selection = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title("Select a process to [red]stop[/]")
                                    .AddChoices(runningProcesses));

                            var processId = selection.Split(':')[0].Trim();
                            await SendControlCommandAsync(processId, "stop");
                            await Task.Delay(1000);
                            await FetchInitialProcessList(ctx);
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[yellow]No running processes to stop[/]");
                        }
                        break;

                    case ConsoleKey.A:
                        // Start a process
                        var stoppedProcesses = _processes.Values
                            .Where(p => !p.IsRunning)
                            .Select(p => $"{p.Id}: {p.Name}")
                            .ToList();

                        if (stoppedProcesses.Count > 0)
                        {
                            var selection = AnsiConsole.Prompt(
                                new SelectionPrompt<string>()
                                    .Title("Select a process to [green]start[/]")
                                    .AddChoices(stoppedProcesses));

                            var processId = selection.Split(':')[0].Trim();
                            await SendControlCommandAsync(processId, "start");
                            await Task.Delay(1000);
                            await FetchInitialProcessList(ctx);
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[yellow]No stopped processes to start[/]");
                        }
                        break;

                    case ConsoleKey.Q:
                        // Quit
                        AnsiConsole.MarkupLine("[grey]Exiting monitor...[/]");
                        _watching = false;
                        return;
                }
            }

            await Task.Delay(100);
        }
    }

    private async Task SendControlCommandAsync(string processId, string commandType)
    {
        try
        {
            AnsiConsole.MarkupLine($"[grey]Sending {commandType} command for process {processId}...[/]");

            var responseChannel = $"ghost:responses:{Guid.NewGuid()}";
            var command = new SystemCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = commandType,
                Parameters = new Dictionary<string, string>
                {
                    ["processId"] = processId,
                    ["responseChannel"] = responseChannel
                }
            };

            await _bus.PublishAsync("ghost:commands", command);

            // Wait for response
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            try
            {
                await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
                {
                    if (response.CommandId == command.CommandId)
                    {
                        if (response.Success)
                        {
                            AnsiConsole.MarkupLine($"[green]Process {processId} {commandType} command sent successfully[/]");

                            // Update process state immediately
                            if (_processes.TryGetValue(processId, out var process))
                            {
                                process.IsRunning = commandType == "start";
                                if (commandType == "start")
                                {
                                    process.StartTime = DateTime.UtcNow;
                                    process.EndTime = null;
                                }
                                else if (commandType == "stop")
                                {
                                    process.EndTime = DateTime.UtcNow;
                                }
                            }
                        }
                        else
                        {
                            _lastError = response.Error ?? $"Failed to {commandType} process";
                            AnsiConsole.MarkupLine($"[red]Process {processId} {commandType} command failed:[/] {response.Error}");
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _lastError = $"Timeout waiting for {commandType} command response";
                AnsiConsole.MarkupLine($"[yellow]Timeout waiting for {commandType} command response[/]");
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            AnsiConsole.MarkupLine($"[red]Error sending {commandType} command:[/] {ex.Message}");
        }
    }
}