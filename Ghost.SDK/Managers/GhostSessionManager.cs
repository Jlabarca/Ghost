using Ghost.Core.Config;
using Ghost.Core.Monitoring;
using Ghost.Father;
using System.Collections.Concurrent;
namespace Ghost.SDK
{
    /// <summary>
    /// Manages Ghost service sessions and application lifecycle
    /// </summary>
    internal class GhostSessionManager : IAsyncDisposable
    {
        private readonly AsyncLocal<GhostApp> _currentApp = new AsyncLocal<GhostApp>();
        private readonly ConcurrentDictionary<string, GhostApp> _registeredApps = new ConcurrentDictionary<string, GhostApp>();
        private readonly ConcurrentDictionary<string, GhostFatherConnection> _connections = new ConcurrentDictionary<string, GhostFatherConnection>();
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        private MetricsManager _metrics;
        private ConfigManager _config;
        private DataManager _data;
        private BusManager _bus;
        private bool _isInitialized;

        /// <summary>
        /// Get the current app for this execution context
        /// </summary>
        public GhostApp CurrentApp => _currentApp.Value;

        /// <summary>
        /// Register a Ghost app with the session manager
        /// </summary>
        public void RegisterApp(GhostApp app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            string appId = app.Config?.App?.Id ?? Guid.NewGuid().ToString();

            // Set as current for this context
            _currentApp.Value = app;

            // Register app
            _registeredApps.TryAdd(appId, app);

            // Initialize services if not already done
            if (!_isInitialized)
            {
                Initialize(app.Config);
            }

            // Create connection if auto-connect is enabled
            if (app.AutoGhostFather)
            {
                CreateConnection(app);
            }
        }

        /// <summary>
        /// Initialize Ghost services with explicit configuration
        /// </summary>
        public void Initialize(GhostConfig config)
        {
            _lock.Wait();
            try
            {
                if (_isInitialized) return;

                // Create service managers
                _config = new ConfigManager(config);
                _bus = new BusManager(config);
                _data = new DataManager(config);
                _metrics = new MetricsManager(_bus.Bus, _data.Data, config);

                _isInitialized = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Create a connection to GhostFather for the app
        /// </summary>
        private void CreateConnection(GhostApp app)
        {
            if (app == null) return;

            string appId = app.Config?.App?.Id ?? Guid.NewGuid().ToString();

            // Create connection if not exists
            if (!_connections.TryGetValue(appId, out _))
            {
                var metadata = new ProcessMetadata(
                    app.Config?.App?.Name ?? "GhostApp",
                    app.IsService ? "service" : "app",
                    app.Config?.App?.Version ?? "1.0.0",
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>
                    {
                        ["AppType"] = app.IsService ? "service" : "one-shot"
                    }
                );

                var connection = new GhostFatherConnection(metadata);
                _connections.TryAdd(appId, connection);

                // Start reporting if auto-monitor is enabled
                if (app.AutoMonitor)
                {
                    connection.StartReporting();
                }
            }
        }

        /// <summary>
        /// Get the metrics manager instance
        /// </summary>
        public MetricsManager GetMetrics() => _metrics;

        /// <summary>
        /// Get the config manager instance
        /// </summary>
        public ConfigManager GetConfig() => _config;

        /// <summary>
        /// Get the data manager instance
        /// </summary>
        public DataManager GetData() => _data;

        /// <summary>
        /// Get the bus manager instance
        /// </summary>
        public BusManager GetBus() => _bus;

        /// <summary>
        /// Shutdown all Ghost services
        /// </summary>
        public async Task ShutdownAsync()
        {
            await _lock.WaitAsync();
            try
            {
                // Dispose all connections
                foreach (var (_, connection) in _connections)
                {
                    await connection.DisposeAsync();
                }
                _connections.Clear();

                // Dispose service managers
                await (_metrics as IAsyncDisposable)?.DisposeAsync().AsTask();
                await (_data as IAsyncDisposable)?.DisposeAsync().AsTask();
                await (_bus as IAsyncDisposable)?.DisposeAsync().AsTask();

                _isInitialized = false;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Dispose all resources
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await ShutdownAsync();
            _lock.Dispose();
        }
    }
}