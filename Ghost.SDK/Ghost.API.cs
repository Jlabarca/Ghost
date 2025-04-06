using Ghost.Core.Config;
namespace Ghost.SDK
{
    /// <summary>
    /// Static facade for accessing Ghost services and utilities
    /// </summary>
    public static class Ghost
    {
        private static readonly GhostSessionManager _sessionManager = new GhostSessionManager();

        /// <summary>
        /// Access to metrics collection and reporting
        /// </summary>
        public static MetricsManager Metrics => _sessionManager.GetMetrics();

        /// <summary>
        /// Access to configuration
        /// </summary>
        public static ConfigManager Config => _sessionManager.GetConfig();

        /// <summary>
        /// Access to data storage
        /// </summary>
        public static DataManager Data => _sessionManager.GetData();

        /// <summary>
        /// Access to messaging bus
        /// </summary>
        public static BusManager Bus => _sessionManager.GetBus();

        /// <summary>
        /// Initialize Ghost services for an application
        /// </summary>
        /// <param name="app">The GhostApp instance to initialize</param>
        public static void Init(GhostApp app)
        {
            _sessionManager.RegisterApp(app);
        }

        /// <summary>
        /// Initialize Ghost services with explicit configuration
        /// </summary>
        /// <param name="config">Configuration override</param>
        public static void Init(GhostConfig config)
        {
            _sessionManager.Initialize(config);
        }

        /// <summary>
        /// Shutdown Ghost services
        /// </summary>
        /// <returns>Task representing the shutdown operation</returns>
        public static async Task ShutdownAsync()
        {
            await _sessionManager.ShutdownAsync();
        }

        /// <summary>
        /// Get the current app instance
        /// </summary>
        public static GhostApp Current => _sessionManager.CurrentApp;

        // Logging methods can stay the same or be moved to a dedicated Logger property
        // G.LogInfo, G.LogError, etc.
    }
}
