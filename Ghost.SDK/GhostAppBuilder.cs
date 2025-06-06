// using Ghost.Config;
//
// namespace Ghost;
//
// /// <summary>
// /// Builder for configuring and creating Ghost applications
// /// </summary>
// public class GhostAppBuilder
// {
//     private Type _appType;
//     private GhostConfig _config;
//     private bool _isService;
//     private bool _autoGhostFather = true;
//     private bool _autoMonitor = true;
//     private bool _autoRestart;
//     private int _maxRestartAttempts = 3;
//     private TimeSpan _tickInterval = TimeSpan.FromSeconds(5);
//
//     /// <summary>
//     /// Specifies the app type to create
//     /// </summary>
//     public GhostAppBuilder UseApp<T>() where T : GhostApp, new()
//     {
//         _appType = typeof(T);
//         return this;
//     }
//
//     /// <summary>
//     /// Sets the configuration for the app
//     /// </summary>
//     public GhostAppBuilder WithConfig(GhostConfig config)
//     {
//         _config = config;
//         return this;
//     }
//
//     /// <summary>
//     /// Configures the app as a service
//     /// </summary>
//     public GhostAppBuilder AsService(bool isService = true)
//     {
//         _isService = isService;
//         return this;
//     }
//
//     /// <summary>
//     /// Configures auto-connection to GhostFather
//     /// </summary>
//     public GhostAppBuilder WithGhostFather(bool autoConnect = true)
//     {
//         _autoGhostFather = autoConnect;
//         return this;
//     }
//
//     /// <summary>
//     /// Configures auto-monitoring
//     /// </summary>
//     public GhostAppBuilder WithMonitoring(bool autoMonitor = true)
//     {
//         _autoMonitor = autoMonitor;
//         return this;
//     }
//
//     /// <summary>
//     /// Configures auto-restart behavior
//     /// </summary>
//     public GhostAppBuilder WithAutoRestart(bool autoRestart = true, int maxAttempts = 3)
//     {
//         _autoRestart = autoRestart;
//         _maxRestartAttempts = maxAttempts;
//         return this;
//     }
//
//     /// <summary>
//     /// Sets the tick interval for services
//     /// </summary>
//     public GhostAppBuilder WithTickInterval(TimeSpan interval)
//     {
//         _tickInterval = interval;
//         return this;
//     }
//
//     /// <summary>
//     /// Builds the configured app
//     /// </summary>
//     public GhostApp Build()
//     {
//         if (_appType == null)
//         {
//             throw new InvalidOperationException("App type must be specified with UseApp<T>()");
//         }
//
//         // Create instance
//         var app = (GhostApp)Activator.CreateInstance(_appType);
//
//         // Apply configuration if provided
//         // if (_config != null)
//         // {
//         //   app.Config = _config;
//         // }
//         //
//         // // Apply settings
//         // app.IsService = _isService;
//         // app.AutoGhostFather = _autoGhostFather;
//         // app.AutoMonitor = _autoMonitor;
//         // app.AutoRestart = _autoRestart;
//         // app.MaxRestartAttempts = _maxRestartAttempts;
//         // app.TickInterval = _tickInterval;
//
//         return app;
//     }
// }