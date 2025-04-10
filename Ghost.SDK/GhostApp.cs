using Ghost.Core.Config;
using Ghost.Core.Logging;
using Ghost.Core.Monitoring;
using Microsoft.Extensions.DependencyInjection;
namespace Ghost.SDK
{
    public class GhostApp : IAsyncDisposable
    {
        protected ServiceCollection Services { get; } = new ServiceCollection();

        /// <summary>
        /// Configuration for the app
        /// </summary>
        public GhostConfig Config { get; private set; }
        public GhostProcess Ghost { get; private set; }
        private GhostFatherConnection Connection { get; set; }

        /// <summary>
        /// Is this a long-running service
        /// </summary>
        public bool IsService { get; protected set; }

        /// <summary>
        /// Should the app automatically connect to GhostFather
        /// </summary>
        public bool AutoGhostFather { get; protected set; } = true;

        /// <summary>
        /// Should the app automatically report metrics
        /// </summary>
        public bool AutoMonitor { get; protected set; } = true;


        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Timer _tickTimer;

        /// <summary>
        /// Time between tick events for periodic processing
        /// </summary>
        public TimeSpan TickInterval { get; protected set; } = TimeSpan.FromSeconds(5);


        /// <summary>
        /// Should the app automatically restart on failure
        /// </summary>
        public bool AutoRestart { get; protected set; }

        /// <summary>
        /// Maximum number of restart attempts (0 = unlimited)
        /// </summary>
        public int MaxRestartAttempts { get; protected set; }

        protected GhostApp(GhostConfig config = null)
        {
            Config = config == null ? LoadConfigFromYaml() : config;

            Ghost = new GhostProcess();
            Ghost.Initialize(this);

            // Apply settings
            ApplyConfigSettings();

            G.LogInfo($"Initialized GhostApp: {GetType().Name}");
        }

        private GhostConfig LoadConfigFromYaml()
        {
            try
            {
                // Look for .ghost.yaml in the current directory
                var yamlPath = Path.Combine(Directory.GetCurrentDirectory(), ".ghost.yaml");
                if (File.Exists(yamlPath))
                {
                    G.LogDebug($"Loading config from: {yamlPath}");
                    // In a real implementation, parse YAML properly
                    // For now, create a minimal default config
                    return new GhostConfig
                    {
                            App = new AppInfo
                            {
                                    Id = Path.GetFileName(Directory.GetCurrentDirectory()),
                                    Name = Path.GetFileName(Directory.GetCurrentDirectory()),
                                    Description = "Ghost Application",
                                    Version = "1.0.0"
                            },
                            Core = new CoreConfig
                            {
                                    Mode = "development",
                                    LogsPath = "logs",
                                    DataPath = "data"
                            },
                    };
                }
            }
            catch (Exception ex)
            {
                G.LogWarn($"Failed to load config from .ghost.yaml: {ex.Message}");
            }

            // Return default config if no file found or error occurred
            return new GhostConfig
            {
                    App = new AppInfo
                    {
                            Id = Path.GetFileName(Directory.GetCurrentDirectory()),
                            Name = Path.GetFileName(Directory.GetCurrentDirectory()),
                            Description = "Ghost Application",
                            Version = "1.0.0"
                    },
                    Core = new CoreConfig
                    {
                            Mode = "development",
                            LogsPath = "logs",
                            DataPath = "data"
                    },
            };
        }

        private void ApplyConfigSettings()
        {
            try
            {
                // Read settings from config.Core.Settings if available
                if (Config.Core != null)
                {
                    // Check for autoGhostFather setting
                    if (Config.Core.Settings.TryGetValue("autoGhostFather", out var autoGF))
                    {
                        AutoGhostFather = !string.Equals(autoGF, "false", StringComparison.OrdinalIgnoreCase);
                    }

                    // Check for autoMonitor setting
                    if (Config.Core.Settings.TryGetValue("autoMonitor", out var autoMon))
                    {
                        AutoMonitor = !string.Equals(autoMon, "false", StringComparison.OrdinalIgnoreCase);
                    }

                    // Check for isService setting
                    if (Config.Core.Settings.TryGetValue("isService", out var isService))
                    {
                        IsService = string.Equals(isService, "true", StringComparison.OrdinalIgnoreCase);
                    }

                    // Check for autoRestart setting
                    if (Config.Core.Settings.TryGetValue("autoRestart", out var autoRestart))
                    {
                        AutoRestart = string.Equals(autoRestart, "true", StringComparison.OrdinalIgnoreCase);
                    }

                    // Check for maxRestartAttempts setting
                    if (Config.Core.Settings.TryGetValue("maxRestartAttempts", out var maxRestarts) &&
                        int.TryParse(maxRestarts, out var maxRestartsValue))
                    {
                        MaxRestartAttempts = maxRestartsValue;
                    }

                    // Check for tickInterval setting
                    if (Config.Core.Settings.TryGetValue("tickInterval", out var tickInterval) &&
                        int.TryParse(tickInterval, out var tickIntervalValue))
                    {
                        TickInterval = TimeSpan.FromSeconds(tickIntervalValue);
                    }
                }
            }
            catch (Exception ex)
            {
                G.LogWarn($"Error applying config settings: {ex.Message}");
            }
        }

        private void InitializeGhostFatherConnection()
        {
            try
            {
                G.LogDebug("Initializing connection to GhostFather...");

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

                // Create connection
                Connection = new GhostFatherConnection(metadata);

                // Start reporting if auto-monitor is enabled
                if (AutoMonitor)
                {
                    Connection.StartReporting();
                }

                G.LogInfo($"Connected to GhostFather: {Config.App.Id}");
            }
            catch (Exception ex)
            {
                G.LogWarn($"Failed to connect to GhostFather: {ex.Message}");
            }
        }

        /// <summary>
        /// Main execution method for the application
        /// </summary>
        public async Task ExecuteAsync(IEnumerable<string> args)
        {
            try
            {
                // Call lifecycle hooks
                await OnBeforeRunAsync();

                // Log start
                G.LogInfo($"Starting {GetType().Name}...");

                // Report running state
                if (Connection != null && AutoMonitor)
                {
                    await Connection.ReportHealthAsync("Starting", "Application is starting");
                }

                // Start tick timer if this is a service
                if (IsService && TickInterval > TimeSpan.Zero)
                {
                    _tickTimer = new Timer(OnTickCallback, null, TimeSpan.Zero, TickInterval);
                }

                // Run the application
                await RunAsync(args);

                // Report completed state for one-shot apps
                if (!IsService && Connection != null && AutoMonitor)
                {
                    await Connection.ReportHealthAsync("Completed", "Application completed successfully");
                }

                // Call lifecycle hooks
                await OnAfterRunAsync();
            }
            catch (Exception ex)
            {
                G.LogError(ex, $"Error executing {GetType().Name}");

                // Report error
                if (Connection != null && AutoMonitor)
                {
                    await Connection.ReportHealthAsync("Error", $"Application error: {ex.Message}");
                }

                // Call error handler
                await OnErrorAsync(ex);

                // Rethrow for upper layers
                throw;
            }
        }

        /// <summary>
        /// Main execution logic - override in derived classes
        /// </summary>
        public virtual Task RunAsync(IEnumerable<string> args)
        {
            G.LogInfo($"Running Ghost application: {GetType().Name}");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called before the main Run method
        /// </summary>
        protected virtual Task OnBeforeRunAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called after the main Run method
        /// </summary>
        protected virtual Task OnAfterRunAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when an error occurs during execution
        /// </summary>
        protected virtual Task OnErrorAsync(Exception ex)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called periodically for service apps
        /// </summary>
        protected virtual Task OnTickAsync()
        {
            return Task.CompletedTask;
        }

        private async void OnTickCallback(object state)
        {
            try
            {
                await OnTickAsync();

                // Report heartbeat
                if (Connection != null && AutoMonitor)
                {
                    await Connection.ReportMetricsAsync();
                }
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Error in tick callback");
            }
        }

        /// <summary>
        /// Stop the application
        /// </summary>
        public async virtual Task StopAsync()
        {
            G.LogInfo($"Stopping {GetType().Name}...");

            // Cancel any operations
            _cts.Cancel();

            // Stop tick timer
            if (_tickTimer != null)
            {
                await _tickTimer.DisposeAsync();
                _tickTimer = null;
            }

            // Report stopping state
            if (Connection != null && AutoMonitor)
            {
                await Connection.ReportHealthAsync("Stopping", "Application is stopping");
            }
        }

        /// <summary>
        /// Cleanup resources
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            // Stop the application if it's still running
            try
            {
                await StopAsync();
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Error stopping application during dispose");
            }

            // Dispose connection
            if (Connection != null)
            {
                await Connection.DisposeAsync();
                Connection = null;
            }

            // Dispose cancellation token source
            _cts.Dispose();
        }
    }
}
