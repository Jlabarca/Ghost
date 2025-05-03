using Ghost.Core;
using Ghost.Core.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using MemoryPack;
using System.Diagnostics;

namespace Ghost.Father.Daemon;

public class GhostFatherDaemon : GhostApp
{
    private ProcessManager _processManager;
    private HealthMonitor _healthMonitor;
    private CommandProcessor _commandProcessor;
    private StateManager _stateManager;
    private AppCommunicationServer _communicationServer;
    private Timer _metricsReportingTimer;
    private bool _isRunning = false;
    private readonly string _daemonId = "ghost-daemon";

    public override async Task RunAsync(IEnumerable<string> args)
    {
        L.LogInfo("GhostFather daemon starting...");
        ConfigureServices();

        // Initialize all services first
        await InitializeServicesAsync();

        // Register itself directly without using GhostFatherConnection
        await RegisterSelfDirectly();

        // Start process manager
        await _processManager.InitializeAsync();

        // Start command processor
        _ = _commandProcessor.StartProcessingAsync(CancellationToken.None);

        // Start communication server
        _ = _communicationServer.StartAsync(CancellationToken.None);

        // Register command handlers
        RegisterCommandHandlers();

        // Discover Ghost apps
        await _processManager.DiscoverGhostAppsAsync();

        // Start metrics reporting timer
        _isRunning = true;
        _metricsReportingTimer = new Timer(ReportMetricsCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        L.LogInfo("GhostFather daemon initialized and ready");
    }

    private async Task InitializeServicesAsync()
    {
        try
        {
            // Initialize base services without connecting to father
            await _stateManager.InitializeAsync();

            L.LogInfo("Services initialized successfully");
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to initialize services");
            throw;
        }
    }

    /// <summary>
    /// Register the daemon directly without going through the normal connection flow
    /// </summary>
    private async Task RegisterSelfDirectly()
    {
        try
        {
            L.LogInfo("Registering daemon process directly");

            var process = Process.GetCurrentProcess();

            // Create registration for the daemon itself
            var registration = new ProcessRegistration
            {
                Id = _daemonId,
                Name = "Ghost Father Daemon",
                Type = "daemon",
                Version = GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0",
                ExecutablePath = process.MainModule?.FileName ?? "unknown",
                Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)),
                WorkingDirectory = Directory.GetCurrentDirectory(),
                Environment = new Dictionary<string, string>
                {
                    ["PID"] = process.Id.ToString()
                },
                Configuration = new Dictionary<string, string>
                {
                    ["AppType"] = "daemon"
                }
            };

            // Register with process manager directly
            await _processManager.RegisterProcessAsync(registration);

            // Also register with communication server
            await _communicationServer.RegisterAppAsync(registration);

            // Create an active connection entry for the daemon
            var connectionInfo = new AppConnectionInfo
            {
                Id = _daemonId,
                Metadata = new ProcessMetadata(
                    registration.Name,
                    registration.Type,
                    registration.Version,
                    registration.Environment,
                    registration.Configuration
                ),
                Status = "Running",
                LastSeen = DateTime.UtcNow,
                IsDaemon = true  // Mark this as the daemon
            };

            // Add to the communication server's connections
            await _communicationServer.RegisterConnectionAsync(connectionInfo);

            L.LogInfo($"Daemon registered with ID: {_daemonId}");
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to register daemon process");
            throw;
        }
    }

    private void ConfigureServices()
    {
        _healthMonitor = new HealthMonitor(Bus);
        _commandProcessor = new CommandProcessor(Bus);
        _stateManager = new StateManager(Data);
        _communicationServer = new AppCommunicationServer(Bus, _healthMonitor, _stateManager);

        Services.AddSingleton(_healthMonitor);
        Services.AddSingleton(_commandProcessor);
        Services.AddSingleton(_stateManager);
        Services.AddSingleton(_communicationServer);

        _processManager = new ProcessManager(Services);
    }

    protected override async Task OnTickAsync()
    {
        if (!_isRunning) return;

        try
        {
            // Process periodic tasks
            await _healthMonitor.CheckHealthAsync();
            await _processManager.MaintenanceTickAsync();
            await _communicationServer.CheckConnectionsAsync();

            // Periodically persist state
            if (DateTime.Now.Second % 5 == 0)
                await _stateManager.PersistStateAsync();
        }
        catch (Exception ex)
        {
            L.LogError("Error in GhostFather tick", ex);
            // Let base class handle restart if needed
            throw;
        }
    }

    protected override async Task OnBeforeRunAsync()
    {
        L.LogInfo("GhostFather preparing to start...");

        // Ensure required directories exist
        Directory.CreateDirectory(Config.GetLogsPath());
        Directory.CreateDirectory(Config.GetDataPath());
        Directory.CreateDirectory(Config.GetAppsPath());

        // Skip the base class OnBeforeRunAsync which tries to connect to father
        // await base.OnBeforeRunAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            L.LogInfo("GhostFather daemon shutting down gracefully...");
            _isRunning = false;

            // Stop the metrics timer
            if (_metricsReportingTimer != null)
            {
                await _metricsReportingTimer.DisposeAsync();
                _metricsReportingTimer = null;
            }

            // Give a moment for final messages to be sent
            await Task.Delay(500);

            // Stop communication server
            await _communicationServer.StopAsync();

            // Stop all processes
            await _processManager.StopAllAsync();

            // Persist final state
            await _stateManager.PersistStateAsync();
        }
        catch (Exception ex)
        {
            L.LogError("Error during GhostFather shutdown", ex);
        }

        base.DisposeAsync();
    }

    private void RegisterCommandHandlers()
    {
        _commandProcessor.RegisterHandler("start", HandleStartCommandAsync);
        _commandProcessor.RegisterHandler("stop", HandleStopCommandAsync);
        _commandProcessor.RegisterHandler("restart", HandleRestartCommandAsync);
        _commandProcessor.RegisterHandler("status", HandleStatusCommandAsync);
        _commandProcessor.RegisterHandler("register", HandleRegisterCommandAsync);
        _commandProcessor.RegisterHandler("run", HandleRunCommandAsync);
        _commandProcessor.RegisterHandler("ping", HandlePingCommandAsync);
        _commandProcessor.RegisterHandler("connections", HandleConnectionsCommandAsync);
    }

    // New command to list currently connected ghost apps
    private async Task HandleConnectionsCommandAsync(SystemCommand cmd)
    {
        try
        {
            L.LogInfo("Getting connected ghost apps");

            var connections = _communicationServer.GetActiveConnections();
            var connectionInfo = connections.Select(c => new
            {
                Id = c.Id,
                Name = c.Metadata.Name,
                Type = c.Metadata.Type,
                Connected = c.LastSeen,
                Status = c.Status,
                AppType = c.Metadata.Configuration.TryGetValue("AppType", out var appType) ? appType : "unknown"
            }).ToList();

            await SendCommandResponseAsync(cmd, true, new ConnectionsResponse
            {
                Connections = connectionInfo,
                Count = connectionInfo.Count
            });
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to get connection information");
            await SendCommandResponseAsync(cmd, false, error: ex.Message);
        }
    }

    // Command handlers
    private async Task HandleRegisterCommandAsync(SystemCommand cmd)
    {
        try
        {
            ProcessRegistration registration = null;

            // First try to get registration from parameters as JSON (legacy format)
            if (cmd.Parameters.TryGetValue("registration", out var registrationJson))
            {
                registration = JsonSerializer.Deserialize<ProcessRegistration>(registrationJson);
            }
            // Then try to get it from the data property as MemoryPack bytes
            else if (!string.IsNullOrEmpty(cmd.Data))
            {
                try
                {
                    // Attempt to deserialize using MemoryPack
                    byte[] data = Convert.FromBase64String(cmd.Data);
                    registration = MemoryPackSerializer.Deserialize<ProcessRegistration>(data);
                }
                catch (Exception ex)
                {
                    L.LogWarn($"Failed to deserialize MemoryPack registration data: {ex.Message}");
                }
            }

            if (registration == null)
            {
                throw new ArgumentException("Registration data is required and could not be parsed");
            }

            var force = cmd.Parameters.TryGetValue("force", out var forceStr) &&
                bool.TryParse(forceStr, out var forceBool) && forceBool;

            L.LogInfo($"Registering process: {registration.Name} ({registration.Id})");

            // Check if process already exists
            try
            {
                var existingProcess = await _processManager.GetProcessAsync(registration.Id);
                if (existingProcess != null && !force)
                {
                    throw new GhostException($"Process {registration.Id} already exists", ErrorCode.ProcessError);
                }
                else if (existingProcess != null)
                {
                    // Stop existing process if it's running
                    if (existingProcess.Status == ProcessStatus.Running)
                    {
                        await _processManager.StopProcessAsync(existingProcess.Id);
                    }
                }
            }
            catch (GhostException ex) when (ex.Code == ErrorCode.ProcessError)
            {
                // Process not found, continue with registration
            }

            // Register process
            await _processManager.RegisterProcessAsync(registration);

            // Register with communication server
            await _communicationServer.RegisterAppAsync(registration);

            // Send success response
            await SendCommandResponseAsync(cmd, true);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to register process");
            await SendCommandResponseAsync(cmd, false, error: ex.Message);
        }
    }

    private async Task HandlePingCommandAsync(SystemCommand cmd)
    {
        try
        {
            // Simple ping response with daemon status information
            var processCount = _processManager.GetAllProcesses().Count();
            var daemonProcess = Process.GetCurrentProcess();
            var connectedApps = _communicationServer.GetActiveConnections().Count();

            var pingResponse = new Dictionary<string, object>
            {
                ["Status"] = "Running",
                ["Uptime"] = (DateTime.UtcNow - daemonProcess.StartTime.ToUniversalTime()).TotalSeconds,
                ["ProcessCount"] = processCount,
                ["ConnectedApps"] = connectedApps,
                ["Memory"] = daemonProcess.WorkingSet64,
                ["Threads"] = daemonProcess.Threads.Count,
                ["Version"] = GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0"
            };

            // Send response directly without serialization
            await SendCommandResponseAsync(cmd, true, data: new StringResponse
            {
                Value = JsonSerializer.Serialize(pingResponse)
            });
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to process ping command");
            await SendCommandResponseAsync(cmd, false, error: ex.Message);
        }
    }

    private async Task HandleStartCommandAsync(SystemCommand cmd)
    {
        try
        {
            if (!cmd.Parameters.TryGetValue("processId", out var processId))
            {
                throw new ArgumentException("Process ID is required");
            }

            L.LogInfo($"Starting process: {processId}");

            // Start the process
            await _processManager.StartProcessAsync(processId);

            // Get the updated process state for response
            var process = await _processManager.GetProcessAsync(processId);
            var state = process?.GetProcessState();

            // Send success response with process state
            await SendProcessStateResponseAsync(cmd, true, null, state);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to start process");
            await SendCommandResponseAsync(cmd, false, error: ex.Message);
        }
    }

    private async Task HandleStopCommandAsync(SystemCommand cmd)
    {
        try
        {
            if (!cmd.Parameters.TryGetValue("processId", out var processId))
            {
                throw new ArgumentException("Process ID is required");
            }

            L.LogInfo($"Stopping process: {processId}");

            // Stop the process
            await _processManager.StopProcessAsync(processId);

            // Get the updated process state for response
            var process = await _processManager.GetProcessAsync(processId);
            var state = process?.GetProcessState();

            // Send success response with process state
            await SendProcessStateResponseAsync(cmd, true, null, state);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to stop process");
            await SendCommandResponseAsync(cmd, false, error: ex.Message);
        }
    }

    private async Task HandleRestartCommandAsync(SystemCommand cmd)
    {
        try
        {
            if (!cmd.Parameters.TryGetValue("processId", out var processId))
            {
                throw new ArgumentException("Process ID is required");
            }

            L.LogInfo($"Restarting process: {processId}");

            // Restart the process (stop then start)
            await _processManager.RestartProcessAsync(processId);

            // Get the updated process state for response
            var process = await _processManager.GetProcessAsync(processId);
            var state = process?.GetProcessState();

            // Send success response with process state
            await SendProcessStateResponseAsync(cmd, true, null, state);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to restart process");
            await SendCommandResponseAsync(cmd, false, error: ex.Message);
        }
    }

    private async Task HandleStatusCommandAsync(SystemCommand cmd)
    {
        try
        {
            L.LogInfo("Getting processes status");

            // Check if a specific process ID was provided
            if (cmd.Parameters.TryGetValue("processId", out var processId))
            {
                // Get status of a specific process
                var process = await _processManager.GetProcessAsync(processId);
                if (process == null)
                {
                    throw new GhostException($"Process {processId} not found", ErrorCode.ProcessError);
                }

                // Send response with single process status
                await SendCommandResponseAsync(cmd, true, data: new ProcessStateResponse { State = process.GetProcessState() });
            }
            else
            {
                // Get status of all processes
                var processes = await _processManager.GetAllProcessesAsync();

                // Convert to ProcessState objects
                var processStates = processes.Select(p => p.GetProcessState()).ToList();

                // Send response with all processes status
                await SendCommandResponseAsync(cmd, true, new ProcessListResponse() { Processes = processStates });
            }
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to get process status");
            await SendCommandResponseAsync(cmd, false, error: ex.Message);
        }
    }

    private async Task HandleRunCommandAsync(SystemCommand cmd)
    {
        try
        {
            if (!cmd.Parameters.TryGetValue("appId", out var appId))
            {
                throw new ArgumentException("App ID is required");
            }

            string workingDirectory = cmd.Parameters.GetValueOrDefault("appPath", Path.Combine(Config.GetAppsPath(), appId));
            bool watch = cmd.Parameters.TryGetValue("watch", out var watchStr) &&
                bool.TryParse(watchStr, out var watchBool) && watchBool;

            L.LogInfo($"Running app: {appId} from {workingDirectory} (watch: {watch})");

            // Check if the app exists
            if (!Directory.Exists(workingDirectory))
            {
                throw new GhostException($"App directory not found: {workingDirectory}", ErrorCode.ProcessError);
            }

            // Determine the executable
            string executablePath;
            string arguments = cmd.Parameters.GetValueOrDefault("args", string.Empty);

            if (OperatingSystem.IsWindows())
            {
                executablePath = Path.Combine(workingDirectory, $"{appId}.exe");
                if (!File.Exists(executablePath))
                {
                    executablePath = "dotnet";
                    arguments = $"{appId}.dll {arguments}";
                }
            }
            else
            {
                executablePath = Path.Combine(workingDirectory, appId);
                if (!File.Exists(executablePath))
                {
                    executablePath = "dotnet";
                    arguments = $"{appId}.dll {arguments}";
                }
            }

            // Create process registration
            var registration = new ProcessRegistration
            {
                Id = appId,
                Name = appId,
                Type = "app",
                Version = "1.0.0",
                ExecutablePath = executablePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Environment = new Dictionary<string, string>(),
                Configuration = new Dictionary<string, string>
                {
                    ["AppType"] = "one-shot",
                    ["watch"] = watch.ToString()
                }
            };

            // Add environment variables from command
            foreach (var param in cmd.Parameters)
            {
                if (param.Key.StartsWith("env:"))
                {
                    registration.Environment[param.Key.Substring(4)] = param.Value;
                }
            }

            // Register and start the process
            await _processManager.RegisterProcessAsync(registration);
            await _processManager.StartProcessAsync(appId);

            // Get the process state
            var process = await _processManager.GetProcessAsync(appId);
            var state = process?.GetProcessState();

            // Send success response with process state
            await SendProcessStateResponseAsync(cmd, true, null, state);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to run app");
            await SendCommandResponseAsync(cmd, false, error: ex.Message);
        }
    }

    private async Task SendCommandResponseAsync(SystemCommand cmd, bool success, ICommandData data = null, string error = null)
    {
        try
        {
            var response = new CommandResponse
            {
                CommandId = cmd.CommandId,
                Success = success,
                Error = error,
                Data = data,
                Timestamp = DateTime.UtcNow
            };

            var responseChannel = cmd.Parameters.GetValueOrDefault("responseChannel", "ghost:responses");

            // Send response directly without additional serialization
            await Bus.PublishAsync(responseChannel, response);
        }
        catch (Exception ex)
        {
            L.LogError("Failed to send command response", ex);
        }
    }

    private Task SendProcessStateResponseAsync(SystemCommand cmd, bool success, string error, ProcessState state)
    {
        var data = state != null ? new ProcessStateResponse { State = state } : null;
        return SendCommandResponseAsync(cmd, success, error: error, data: data);
    }

    private Task SendBooleanResponseAsync(SystemCommand cmd, bool success, string error, bool value)
    {
        return SendCommandResponseAsync(cmd, success, error: error, data: new BooleanResponse { Value = value });
    }

    // Periodic task to report metrics for all processes
    private async void ReportMetricsCallback(object state)
    {
        if (!_isRunning) return;

        try
        {
            // Get all running processes
            var processes = _processManager.GetAllProcesses().Where(p => p.Status == ProcessStatus.Running).ToList();

            foreach (var process in processes)
            {
                try
                {
                    // Only report metrics for processes with a valid ID
                    if (string.IsNullOrEmpty(process.Id)) continue;

                    // For the daemon itself, report metrics directly
                    if (process.Id == _daemonId)
                    {
                        var daemonMetrics = GetDaemonMetrics();
                        // Update metrics in communication server directly
                        _communicationServer.UpdateDaemonMetrics(daemonMetrics);
                        // Also publish for any interested clients
                        await Bus.PublishAsync($"ghost:metrics:{_daemonId}", daemonMetrics);
                        continue;
                    }

                    // Create metrics from process state
                    var metrics = CreateProcessMetrics(process);

                    // For external apps, serialize with MemoryPack
                    await Bus.PublishAsync($"ghost:metrics:{process.Id}", MemoryPackSerializer.Serialize(metrics));
                }
                catch (Exception ex)
                {
                    L.LogError(ex, $"Error reporting metrics for process {process.Id}");
                }
            }
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Error in metrics reporting");
        }
    }

    // Helper to create process metrics
    private ProcessMetrics CreateProcessMetrics(ProcessInfo process)
    {
        // Check if we have connection info from the communication server
        var connectionInfo = _communicationServer.GetConnectionInfoById(process.Id);
        if (connectionInfo != null && connectionInfo.LastMetrics != null)
        {
            return connectionInfo.LastMetrics;
        }

        // Try to get actual process metrics if available
        System.Diagnostics.Process systemProcess = null;

        try
        {
            int pid = 0;
            if (process.Metadata.Environment.TryGetValue("PID", out var pidStr) &&
                int.TryParse(pidStr, out pid) && pid > 0)
            {
                systemProcess = System.Diagnostics.Process.GetProcessById(pid);
            }
        }
        catch
        {
            // Process might not be found or accessible, ignore errors
        }

        if (systemProcess != null)
        {
            systemProcess.Refresh();
            return new ProcessMetrics(
                ProcessId: process.Id,
                CpuPercentage: 0, // Calculating CPU percentage requires multiple samples
                MemoryBytes: systemProcess.WorkingSet64,
                ThreadCount: systemProcess.Threads.Count,
                Timestamp: DateTime.UtcNow,
                HandleCount: systemProcess.HandleCount,
                GcTotalMemory: GC.GetTotalMemory(false),
                Gen0Collections: GC.CollectionCount(0),
                Gen1Collections: GC.CollectionCount(1),
                Gen2Collections: GC.CollectionCount(2)
            );
        }

        // Fallback to estimated metrics
        return new ProcessMetrics(
            ProcessId: process.Id,
            CpuPercentage: 0,
            MemoryBytes: 0,
            ThreadCount: 0,
            Timestamp: DateTime.UtcNow,
            HandleCount: 0,
            GcTotalMemory: 0,
            Gen0Collections: 0,
            Gen1Collections: 0,
            Gen2Collections: 0
        );
    }

    // Get metrics for the daemon itself
    private ProcessMetrics GetDaemonMetrics()
    {
        var process = Process.GetCurrentProcess();
        return new ProcessMetrics(
            ProcessId: _daemonId,
            CpuPercentage: 0, // Proper CPU calculation would need time tracking
            MemoryBytes: process.WorkingSet64,
            ThreadCount: process.Threads.Count,
            Timestamp: DateTime.UtcNow,
            HandleCount: process.HandleCount,
            GcTotalMemory: GC.GetTotalMemory(false),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2)
        );
    }
}

/// <summary>
/// Represents information about a connected ghost app
/// </summary>
public class AppConnectionInfo
{
    public string Id { get; set; }
    public ProcessMetadata Metadata { get; set; }
    public string Status { get; set; }
    public string LastMessage { get; set; }
    public DateTime LastSeen { get; set; }
    public ProcessMetrics LastMetrics { get; set; }
    public bool IsDaemon { get; set; } = false;
}

/// <summary>
/// Response containing connection information
/// </summary>
[MemoryPackable]
public partial class ConnectionsResponse : ICommandData
{
    public IEnumerable<object> Connections { get; set; }
    public int Count { get; set; }
}