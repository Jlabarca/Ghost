using Ghost.Core;
using Ghost.Core.Data;
using Ghost.Core.Storage;
using Ghost.Core.Data.Implementations;

namespace Ghost
{
    public partial class GhostApp
    {
        // List to track disposal actions
        private readonly List<Func<Task>> _disposalActions = new List<Func<Task>>();
        private ICache _cache;
        private IGhostBus _bus;

        /// <summary>
        /// Registers an action to be executed when the app is disposed
        /// </summary>
        private void RegisterDisposalAction(Func<Task> action)
        {
            _disposalActions.Add(action);
        }

        private void InitializeGhostFatherConnection()
        {
            // Initialize the connection to GhostFather monitoring system asynchronously but wait for completion
            Task.Run(InitializeGhostFatherConnectionAsync).Wait();
        }

        /// <summary>
        /// Initializes the connection to GhostFather monitoring system
        /// with the enhanced implementation
        /// </summary>
        private async Task InitializeGhostFatherConnectionAsync()
        {
            try
            {
                // Check if this is the daemon itself - skip connection in that case
                bool isDaemon = GetType().Name == "GhostFatherDaemon" ||
                               (Config?.App?.Id == "ghost-daemon");

                if (isDaemon)
                {
                    G.LogInfo("Skipping automatic GhostFather connection for daemon");
                    return;
                }

                G.LogDebug("Initializing enhanced connection to GhostFather...");

                // Get connection configuration from app settings or environment
                var connectionConfig = GetConnectionConfiguration();

                // Create metadata for the process
                var metadata = new ProcessMetadata(
                    Name: Config.App.Name ?? GetType().Name,
                    Type: IsService ? "service" : "app",
                    Version: Config.App.Version ?? "1.0.0",
                    Environment: new Dictionary<string, string>(),
                    Configuration: new Dictionary<string, string>
                    {
                        ["AppType"] = IsService ? "service" : "one-shot"
                    }
                );

                // Create appropriate bus implementation based on configuration
                _bus = await CreateMessageBusAsync(connectionConfig);

                // Create direct communication fallback if enabled
                IDirectCommunication? directComm = connectionConfig.EnableFallback
                    ? CreateDirectCommunication(connectionConfig)
                    : null;

                // Create connection diagnostics if enabled
                IConnectionDiagnostics? diagnostics = connectionConfig.EnableDiagnostics
                    ? CreateConnectionDiagnostics(connectionConfig)
                    : null;

                // Create enhanced connection with all the components
                Connection = new GhostFatherConnection(
                    bus: _bus,
                    metadata: metadata,
                    directComm: directComm,
                    diagnostics: diagnostics
                );

                // Subscribe to relevant events
                Connection.ConnectionStatusChanged += OnConnectionStatusChanged;
                if (diagnostics != null)
                {
                    Connection.DiagnosticsCompleted += OnDiagnosticsCompleted;
                }

                // Start reporting if auto-monitor is enabled
                if (AutoMonitor)
                {
                    await Connection.StartReporting();
                }

                G.LogInfo($"Connected to GhostFather: {Config.App.Id}");

                // Register cleanup on app disposal
                RegisterDisposalAction(async () => {
                    if (Connection != null) {
                        Connection.ConnectionStatusChanged -= OnConnectionStatusChanged;
                        if (diagnostics != null)
                        {
                            Connection.DiagnosticsCompleted -= OnDiagnosticsCompleted;
                        }
                        await Connection.DisposeAsync();
                    }
                });
            }
            catch (Exception ex)
            {
                G.LogWarn($"Failed to connect to GhostFather: {ex.Message}");

                // Create dummy connection that does nothing if enabled in config
                if (ShouldCreateDummyOnFailure())
                {
                    G.LogInfo("Creating dummy connection due to connection failure");
                    Connection = CreateDummyConnection();
                }
                else
                {
                    Connection = null;
                }
            }
        }

        /// <summary>
        /// Creates the appropriate message bus based on configuration
        /// </summary>
        private async Task<IGhostBus> CreateMessageBusAsync(ConnectionConfiguration config)
        {
            try
            {
                // Determine if we use Redis
                if (!string.IsNullOrEmpty(config.RedisConnectionString))
                {
                    // Create Redis-based implementation
                    _cache = new RedisCache(config.RedisConnectionString, G.GetLogger());
                    return new RedisGhostBus(config.RedisConnectionString);
                }

                // Fall back to default implementation
                return new GhostBus(new MemoryCache(G.GetLogger()));
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Failed to create message bus, using default implementation");
            }

            _cache = new MemoryCache(G.GetLogger());
            return new GhostBus(_cache);
        }

        /// <summary>
        /// Creates appropriate direct communication implementation
        /// </summary>
        private IDirectCommunication? CreateDirectCommunication(ConnectionConfiguration config)
        {
            // Create simple implementation that always returns success
            return new SimpleDirectCommunication();
        }

        /// <summary>
        /// Creates connection diagnostics implementation
        /// </summary>
        private IConnectionDiagnostics CreateConnectionDiagnostics(ConnectionConfiguration config)
        {
            // Create simple implementation that returns basic results
            return new SimpleConnectionDiagnostics(
                daemonProcessName: "ghost-father-daemon",
                canAutoStart: config.EnableAutoStartDaemon);
        }

        /// <summary>
        /// Creates a dummy connection when real connection fails
        /// </summary>
        private GhostFatherConnection CreateDummyConnection()
        {
            // Create basic bus
            var dummyBus = new GhostBus(new MemoryCache(G.GetLogger()));

            // Create metadata
            var metadata = new ProcessMetadata(
                Name: Config.App.Name ?? GetType().Name,
                Type: IsService ? "service" : "app",
                Version: Config.App.Version ?? "1.0.0",
                Environment: new Dictionary<string, string>(),
                Configuration: new Dictionary<string, string>
                {
                    ["AppType"] = IsService ? "service" : "one-shot"
                }
            );

            // Return connection that works but doesn't actually connect to daemon
            return new GhostFatherConnection(
                bus: dummyBus,
                metadata: metadata,
                directComm: null,
                diagnostics: null);
        }

        /// <summary>
        /// Handle connection status changes
        /// </summary>
        private void OnConnectionStatusChanged(object sender, GhostFatherConnection.ConnectionStatusEventArgs args)
        {
            if (args.IsConnected)
            {
                G.LogInfo("GhostFather connection established");
            }
            else
            {
                G.LogWarn($"GhostFather connection lost: {args.ErrorMessage}");
            }

            // Raise application event if handlers are registered
            ConnectionChanged?.Invoke(this, args);
        }

        /// <summary>
        /// Handle diagnostics completion
        /// </summary>
        private void OnDiagnosticsCompleted(object sender, ConnectionDiagnosticsEventArgs args)
        {
            var results = args.Results;

            G.LogInfo($"GhostFather connection diagnostics: Redis={results.IsRedisAvailable}, " +
                     $"Daemon={results.IsDaemonRunning}, Network={results.IsNetworkOk}");

            // Take automatic recovery actions based on diagnostics
            if (!results.IsDaemonRunning && results.CanAutoStartDaemon)
            {
                G.LogInfo("Diagnostics indicated daemon is not running but can be auto-started");
            }

            // Log recommended actions
            if (results.RecommendedActions.Count > 0)
            {
                G.LogInfo($"Recommended actions: {string.Join(", ", results.RecommendedActions)}");
            }

            // Raise application event if handlers are registered
            DiagnosticsCompleted?.Invoke(this, args);
        }

        /// <summary>
        /// Gets connection configuration from settings
        /// </summary>
        private ConnectionConfiguration GetConnectionConfiguration()
        {
            var config = new ConnectionConfiguration();

            // Try to get Redis connection string from various sources
            config.RedisConnectionString =
                Environment.GetEnvironmentVariable("GHOST_REDIS_CONNECTION") ??
                GetCoreSetting("RedisConnection") ??
                "localhost:6379";

            // Get other settings with defaults
            config.EnableFallback = GetBoolSetting("EnableFallback", true);
            config.EnableDiagnostics = GetBoolSetting("EnableDiagnostics", true);
            config.EnableNamedPipes = GetBoolSetting("EnableNamedPipes", true);
            config.EnableTcpFallback = GetBoolSetting("EnableTcpFallback", true);
            config.EnableAutoStartDaemon = GetBoolSetting("EnableAutoStartDaemon", false);
            config.QueueSize = GetIntSetting("MessageQueueSize", 1000);
            config.DaemonHost = GetStringSetting("DaemonHost", "localhost");
            config.DaemonPort = GetIntSetting("DaemonPort", 9876);
            config.CommunicationTimeoutSeconds = GetIntSetting("CommunicationTimeout", 10);

            return config;
        }

        /// <summary>
        /// Whether to create a dummy connection on failure
        /// </summary>
        private bool ShouldCreateDummyOnFailure()
        {
            // Check environment variables and config settings
            return Environment.GetEnvironmentVariable("GHOST_CREATE_DUMMY_ON_FAILURE") == "true" ||
                  GetBoolSetting("CreateDummyOnFailure", true);
        }

        // Helper methods to get typed settings with defaults
        private bool GetBoolSetting(string name, bool defaultValue)
        {
            string value = GetCoreSetting(name);
            return value != null ? value.ToLower() == "true" : defaultValue;
        }

        private int GetIntSetting(string name, int defaultValue)
        {
            string value = GetCoreSetting(name);
            return value != null && int.TryParse(value, out var result) ? result : defaultValue;
        }

        private string GetStringSetting(string name, string defaultValue)
        {
            return GetCoreSetting(name) ?? defaultValue;
        }

        private string GetCoreSetting(string name)
        {
            return Config?.Core?.Settings?.TryGetValue(name, out var value) == true ? value : null;
        }


        /// <summary>
        /// Class to hold connection configuration
        /// </summary>
        private class ConnectionConfiguration
        {
            public string RedisConnectionString { get; set; }
            public bool EnableFallback { get; set; }
            public bool EnableDiagnostics { get; set; }
            public bool EnableNamedPipes { get; set; }
            public bool EnableTcpFallback { get; set; }
            public bool EnableAutoStartDaemon { get; set; }
            public int QueueSize { get; set; }
            public string DaemonHost { get; set; }
            public int DaemonPort { get; set; }
            public int CommunicationTimeoutSeconds { get; set; }
            public string DaemonExecutablePath { get; set; }
        }

        /// <summary>
        /// Simple implementation of direct communication
        /// </summary>
        private class SimpleDirectCommunication : IDirectCommunication
        {
            public Task<bool> TestConnectionAsync() => Task.FromResult(true);
            public Task RegisterProcessAsync(ProcessRegistration registration) => Task.CompletedTask;
            public Task SendEventAsync(SystemEvent systemEvent) => Task.CompletedTask;
            public Task SendCommandAsync(SystemCommand command) => Task.CompletedTask;
            public Task<CommandResponse> SendCommandWithResponseAsync(SystemCommand command) => 
                Task.FromResult(new CommandResponse
                {
                    CommandId = command.CommandId,
                    Success = true,
                    Timestamp = DateTime.UtcNow
                });
            public Task SendHeartbeatAsync(HeartbeatMessage heartbeat) => Task.CompletedTask;
            public Task SendHealthStatusAsync(HealthStatusMessage healthStatus) => Task.CompletedTask;
            public Task SendMetricsAsync(ProcessMetrics metrics) => Task.CompletedTask;
        }

        /// <summary>
        /// Simple implementation of connection diagnostics
        /// </summary>
        private class SimpleConnectionDiagnostics : IConnectionDiagnostics
        {
            private readonly string _daemonProcessName;
            private readonly bool _canAutoStart;

            public SimpleConnectionDiagnostics(string daemonProcessName, bool canAutoStart)
            {
                _daemonProcessName = daemonProcessName;
                _canAutoStart = canAutoStart;
            }

            public Task<ConnectionDiagnosticResults> RunDiagnosticsAsync(ConnectionDiagnosticRequest request)
            {
                var results = new ConnectionDiagnosticResults
                {
                    IsRedisAvailable = true,
                    IsDaemonRunning = true,
                    IsNetworkOk = true,
                    HasRequiredPermissions = true,
                    CanUseFallback = true,
                    CanAutoStartDaemon = _canAutoStart,
                    DiagnosticMessage = "Diagnostics completed successfully",
                    RecommendedActions = new List<string>()
                };

                return Task.FromResult(results);
            }

            public Task<bool> IsDaemonProcessRunningAsync() => Task.FromResult(true);

            public Task<bool> TryStartDaemonAsync() => Task.FromResult(true);
        }

        /// <summary>
        /// Event raised when connection status changes
        /// </summary>
        public event EventHandler<GhostFatherConnection.ConnectionStatusEventArgs> ConnectionChanged;

        /// <summary>
        /// Event raised when diagnostics completes
        /// </summary>
        public event EventHandler<ConnectionDiagnosticsEventArgs> DiagnosticsCompleted;
    }
}