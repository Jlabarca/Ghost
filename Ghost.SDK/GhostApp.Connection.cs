using Ghost.Storage;

namespace Ghost;

public abstract partial class GhostApp
{
#region GhostFather Connection (Optional)

    protected GhostFatherConnection Connection { get; private set; }

    /// <summary>
    /// Event raised when connection status to GhostFather changes.
    /// </summary>
    public event EventHandler<GhostFatherConnection.ConnectionStatusEventArgs> GhostFatherConnectionChanged;

    /// <summary>
    /// Event raised when GhostFather connection diagnostics complete.
    /// </summary>
    public event EventHandler<ConnectionDiagnosticsEventArgs> GhostFatherDiagnosticsCompleted;


    private async Task InitializeGhostFatherConnectionAsync()
    {
        G.LogInfo("Attempting to initialize GhostFather connection...");
        try
        {
            var processMetadata = new ProcessMetadata(
                    Name: Config.App.Name ?? GetType().Name,
                    Type: IsService ? "service" : "app",
                    Version: Config.App.Version ?? "1.0.0",
                    Environment: new Dictionary<string, string>(), // Populate if needed
                    Configuration: new Dictionary<string, string>
                    {
                            ["AppType"] = IsService ? "service" : "one-shot"
                    }
            );

            var processInfo = new ProcessInfo(
                    id: Config.App.Id,
                    metadata: processMetadata,
                    executablePath: Environment.ProcessPath ?? string.Empty,
                    arguments: string.Join(" ", GetCommandLineArgsSkipFirst()),
                    workingDirectory: Directory.GetCurrentDirectory(),
                    environment: new Dictionary<string, string>() // Populate if needed
            );

            // The Bus is now resolved via DI (_busLazy.Value)
            if (Bus == null)
            {
                G.LogWarn("Message Bus (IGhostBus) is not available. GhostFather connection cannot be established.");
                Connection = null;
                return;
            }

            var connectionConfig = GetConnectionConfiguration(Config); // Ensure this method is accessible


            // For simplicity in this refactor, directComm and diagnostics are not implemented here.
            // In a real scenario, you'd resolve or create these, possibly via DI if they are complex.
            IDirectCommunication directComm = connectionConfig.EnableFallback ? new SimpleDirectCommunication() : null;
            IConnectionDiagnostics diagnostics = connectionConfig.EnableDiagnostics ? new SimpleConnectionDiagnostics(this) : null;

            Connection = new GhostFatherConnection(
                    bus: Bus, // Use the DI-resolved bus
                    processInfo: processInfo,
                    directComm: directComm, // Simplified for now
                    diagnostics: diagnostics // Simplified for now
            );

            Connection.ConnectionStatusChanged += OnGhostFatherConnectionStatusChanged;
            if (diagnostics != null)
            {
                Connection.DiagnosticsCompleted += OnGhostFatherDiagnosticsCompleted;
            }

            if (AutoMonitor) // AutoMonitor is a GhostApp setting
            {
                await Connection.StartReporting();
                G.LogInfo($"GhostFather reporting started for {Config.App.Id}.");
            }
            else
            {
                G.LogInfo($"GhostFather reporting is disabled for {Config.App.Id}.");
            }

            RegisterDisposalAction(async () =>
            {
                if (Connection != null)
                {
                    Connection.ConnectionStatusChanged -= OnGhostFatherConnectionStatusChanged;
                    if (diagnostics != null) Connection.DiagnosticsCompleted -= OnGhostFatherDiagnosticsCompleted;
                    await Connection.DisposeAsync();
                }
            });
        }
        catch (Exception ex)
        {
            G.LogWarn($"Failed to initialize GhostFather connection: {ex.Message}. Application will run in offline mode.");
            Connection = null; // Ensure connection is null on failure
        }
    }

    protected IEnumerable<string?> GetCommandLineArgsSkipFirst()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Length > 1 ? new ArraySegment<string>(args, 1, args.Length - 1) : Array.Empty<string>();
    }

    private void OnGhostFatherConnectionStatusChanged(object sender, GhostFatherConnection.ConnectionStatusEventArgs e)
    {
        if (e.IsConnected) G.LogInfo($"GhostFather connection established for {Config.App.Id}.");
        else G.LogWarn($"GhostFather connection lost for {Config.App.Id}: {e.ErrorMessage}");
        GhostFatherConnectionChanged?.Invoke(this, e);
    }

    private void OnGhostFatherDiagnosticsCompleted(object sender, ConnectionDiagnosticsEventArgs e)
    {
        G.LogInfo($"GhostFather diagnostics for {Config.App.Id}: Redis={e.Results.IsRedisAvailable}, Daemon={e.Results.IsDaemonRunning}, Network={e.Results.IsNetworkOk}");
        GhostFatherDiagnosticsCompleted?.Invoke(this, e);
    }

    protected async Task ReportHealthAsync(string status, string message)
    {
        if (Connection != null && Connection.IsConnected && AutoMonitor)
        {
            try
            {
                await Connection.ReportHealthAsync(status, message);
            }
            catch (Exception ex)
            {
                G.LogWarn($"Failed to report health to GhostFather: {ex.Message}");
            }
        }
        else if (AutoGhostFather && AutoMonitor) // Log only if it was supposed to report
        {
            G.LogDebug($"Offline mode: Health report skipped (Status: {status}, Message: {message})");
        }
    }

    protected async Task ReportMetricsAsync()
    {
        if (Connection != null && Connection.IsConnected && AutoMonitor)
        {
            try
            {
                // Metrics collection would happen here or be passed in
                await Connection.ReportMetricsAsync( /* pass collected ProcessMetrics here */);
            }
            catch (Exception ex)
            {
                G.LogWarn($"Failed to report metrics to GhostFather: {ex.Message}");
            }
        }
        else if (AutoGhostFather && AutoMonitor)
        {
            G.LogDebug("Offline mode: Metrics report skipped.");
        }
    }

    // Simplified IDirectCommunication and IConnectionDiagnostics for this example
    // In a real app, these might be more complex or provided via DI.
    private class SimpleDirectCommunication : IDirectCommunication
    {
        public Task<bool> TestConnectionAsync() => Task.FromResult(false); // Default to false for offline
        public Task RegisterProcessAsync(ProcessRegistration registration) => Task.CompletedTask;
        public Task SendEventAsync(SystemEvent systemEvent) => Task.CompletedTask;
        public Task SendCommandAsync(SystemCommand command) => Task.CompletedTask;
        public Task<CommandResponse> SendCommandWithResponseAsync(SystemCommand command) =>
                Task.FromResult(new CommandResponse
                {
                        CommandId = command.CommandId,
                        Success = false,
                        Error = "Offline mode"
                });
        public Task SendHeartbeatAsync(HeartbeatMessage heartbeat) => Task.CompletedTask;
        public Task SendHealthStatusAsync(HealthStatusMessage healthStatus) => Task.CompletedTask;
        public Task SendMetricsAsync(ProcessMetrics metrics) => Task.CompletedTask;
    }

    private class SimpleConnectionDiagnostics : IConnectionDiagnostics
    {
        private readonly GhostApp _app;
        public SimpleConnectionDiagnostics(GhostApp app) { _app = app; }
        public Task<ConnectionDiagnosticResults> RunDiagnosticsAsync(ConnectionDiagnosticRequest request) =>
                Task.FromResult(new ConnectionDiagnosticResults
                {
                        IsDaemonRunning = false,
                        IsRedisAvailable = _app.Bus is RedisGhostBus
                });
        public Task<bool> IsDaemonProcessRunningAsync() => Task.FromResult(false);
        public Task<bool> TryStartDaemonAsync() => Task.FromResult(false);
    }

#endregion
}
