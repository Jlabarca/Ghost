// using Ghost.Core.Config;
// using Ghost.Core.Data;
// using Ghost.Core.Monitoring;
// using Ghost.Core.Storage;
//
// namespace Ghost;
//
// /// <summary>
// /// Static API for accessing Ghost functionality
// /// </summary>
// public static partial class G
// {
//
//         #region Direct Access to Subsystems
//
//   /// <summary>
//   /// Direct access to the configuration
//   /// </summary>
//   public static GhostConfig Config => GhostProcess.Instance.Config;
//
//   /// <summary>
//   /// Direct access to the data layer
//   /// </summary>
//   public static IGhostData Data => GhostProcess.Instance.Data;
//
//   /// <summary>
//   /// Direct access to the message bus
//   /// </summary>
//   public static IGhostBus Bus => GhostProcess.Instance.Bus;
//
//   /// <summary>
//   /// Direct access to the metrics collector
//   /// </summary>
//   public static IMetricsCollector Metrics => GhostProcess.Instance.Metrics;
//
//         #endregion
//
//         #region Current App Context
//
//   /// <summary>
//   /// Access to the current application
//   /// </summary>
//   public static GhostApp Current => GhostProcess.Instance.CurrentApp;
//
//         #endregion
//
//         #region Initialization
//
//   /// <summary>
//   /// Initialize the Ghost system with an application
//   /// </summary>
//   /// <param name="app">The application to initialize with</param>
//   public static void Init(GhostApp app)
//   {
//     GhostProcess.Instance.Initialize(app);
//   }
//
//         #endregion
//
//         #region Metrics Methods
//
//   /// <summary>
//   /// Track a metric value
//   /// </summary>
//   public static Task TrackMetricAsync(string name, double value, Dictionary<string, string> tags = null) =>
//       GhostProcess.Instance.TrackMetricAsync(name, value, tags);
//
//   /// <summary>
//   /// Track a named event
//   /// </summary>
//   public static Task TrackEventAsync(string name, Dictionary<string, string> properties = null) =>
//       GhostProcess.Instance.TrackEventAsync(name, properties);
//
//         #endregion
//
//         #region Data Methods
//
//   /// <summary>
//   /// Execute a SQL command
//   /// </summary>
//   public static Task<int> ExecuteAsync(string sql, object param = null) =>
//       GhostProcess.Instance.ExecuteAsync(sql, param);
//
//   /// <summary>
//   /// Query for data
//   /// </summary>
//   public static Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null) =>
//       GhostProcess.Instance.QueryAsync<T>(sql, param);
//
//   /// <summary>
//   /// Get a configuration setting
//   /// </summary>
//   public static string GetSetting(string name, string defaultValue = null) =>
//       GhostProcess.Instance.GetSetting(name, defaultValue);
//
//         #endregion
//
//         #region Bus Methods
//
//   /// <summary>
//   /// Publish a message
//   /// </summary>
//   public static Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null) =>
//       GhostProcess.Instance.PublishAsync(channel, message, expiry);
//
//   /// <summary>
//   /// Subscribe to a channel
//   /// </summary>
//   public static IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, CancellationToken cancellationToken = default) =>
//       GhostProcess.Instance.SubscribeAsync<T>(channelPattern, cancellationToken);
//
//         #endregion
//
//         #region Shutdown
//
//   /// <summary>
//   /// Shutdown the Ghost system
//   /// </summary>
//   public static Task ShutdownAsync() =>
//       GhostProcess.Instance.ShutdownAsync();
//
//         #endregion
//
// }
