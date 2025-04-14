using Ghost.Core.Config;
using Ghost.Core.Logging;
using Ghost.Core.Monitoring;
using Microsoft.Extensions.DependencyInjection;

namespace Ghost
{
    /// <summary>
    /// Specialized GhostApp for long-running services with extended monitoring
    /// </summary>
    [GhostApp(IsService = true, AutoMonitor = true, AutoRestart = true)]
    public class GhostServiceApp : GhostApp
    {
        private readonly SemaphoreSlim _serviceLock = new SemaphoreSlim(1, 1);
        protected int _restartAttempts = 0;
        protected DateTime? _lastRestartTime;
        
        /// <summary>
        /// Service health status
        /// </summary>
        public HealthStatus HealthStatus { get; protected set; } = HealthStatus.Unknown;
        
        /// <summary>
        /// Service collection for dependency injection
        /// </summary>
        protected ServiceCollection Services { get; } = new ServiceCollection();
        
        /// <summary>
        /// Health monitor for service state
        /// </summary>
        protected HealthMonitor HealthMonitor { get; private set; }
        
        /// <summary>
        /// Metrics collector for performance tracking
        /// </summary>
        protected MetricsCollector Metrics { get; private set; }

        /// <summary>
        /// Initialize a new service app
        /// </summary>
        /// <param name="config">Optional configuration override</param>
        public GhostServiceApp() : base()
        {
            // Configure services
            ConfigureServices();
            
            // Initialize components
            try
            {
                InitializeComponents();
            }
            catch (Exception ex)
            {
                L.LogError(ex, "Failed to initialize service components");
            }
            
            // Register state change handler
            StateChanged += OnServiceStateChanged;
        }

        /// <summary>
        /// Event handler for service state changes
        /// </summary>
        private async void OnServiceStateChanged(object sender, GhostAppState newState)
        {
            switch (newState)
            {
                case GhostAppState.Starting:
                    await HandleServiceStartingAsync();
                    break;
                case GhostAppState.Running:
                    await HandleServiceRunningAsync();
                    break;
                case GhostAppState.Stopping:
                    await HandleServiceStoppingAsync();
                    break;
                case GhostAppState.Stopped:
                    await HandleServiceStoppedAsync();
                    break;
                case GhostAppState.Failed:
                    await HandleServiceFailedAsync();
                    break;
            }
        }

        /// <summary>
        /// Handle service starting state
        /// </summary>
        protected virtual async Task HandleServiceStartingAsync()
        {
            if (HealthMonitor != null)
            {
                await HealthMonitor.ReportHealthAsync(new HealthReport(
                    Status: HealthStatus.Unknown,
                    Message: "Service is starting",
                    Metrics: new Dictionary<string, object>(),
                    Timestamp: DateTime.UtcNow
                ));
            }
        }

        /// <summary>
        /// Handle service running state
        /// </summary>
        protected virtual async Task HandleServiceRunningAsync()
        {
            if (HealthMonitor != null)
            {
                await HealthMonitor.ReportHealthAsync(new HealthReport(
                    Status: HealthStatus.Healthy,
                    Message: "Service is running",
                    Metrics: new Dictionary<string, object>(),
                    Timestamp: DateTime.UtcNow
                ));
            }
            
            // Start metrics collector if available
            if (Metrics != null)
            {
                await Metrics.StartAsync();
            }
        }

        /// <summary>
        /// Handle service stopping state
        /// </summary>
        protected virtual async Task HandleServiceStoppingAsync()
        {
            if (HealthMonitor != null)
            {
                await HealthMonitor.ReportHealthAsync(new HealthReport(
                    Status: HealthStatus.Unknown,
                    Message: "Service is stopping",
                    Metrics: new Dictionary<string, object>(),
                    Timestamp: DateTime.UtcNow
                ));
            }
            
            // Stop metrics collector if available
            if (Metrics != null)
            {
                await Metrics.StopAsync();
            }
        }

        /// <summary>
        /// Handle service stopped state
        /// </summary>
        protected virtual async Task HandleServiceStoppedAsync()
        {
            if (HealthMonitor != null)
            {
                await HealthMonitor.ReportHealthAsync(new HealthReport(
                    Status: HealthStatus.Unknown,
                    Message: "Service has stopped",
                    Metrics: new Dictionary<string, object>(),
                    Timestamp: DateTime.UtcNow
                ));
            }
        }

        /// <summary>
        /// Handle service failed state
        /// </summary>
        protected virtual async Task HandleServiceFailedAsync()
        {
            if (HealthMonitor != null)
            {
                await HealthMonitor.ReportHealthAsync(new HealthReport(
                    Status: HealthStatus.Unhealthy,
                    Message: "Service has failed",
                    Metrics: new Dictionary<string, object>(),
                    Timestamp: DateTime.UtcNow
                ));
            }
        }

        /// <summary>
        /// Configure dependency injection services
        /// </summary>
        protected virtual void ConfigureServices()
        {
            // Add configuration
            Services.AddSingleton(Config);
            
            // Configure dependencies based on whether auto-monitoring is enabled
            if (AutoGhostFather && AutoMonitor)
            {
                ConfigureMonitoringServices();
            }
        }

        /// <summary>
        /// Configure monitoring-related services
        /// </summary>
        protected virtual void ConfigureMonitoringServices()
        {
            try
            {
                // Configure health monitor
                var healthMonitor = new HealthMonitor(Config.Core.HealthCheckInterval);
                Services.AddSingleton<HealthMonitor>(healthMonitor);
                
                // Configure metrics collector
                var metricsCollector = new MetricsCollector(Config.Core.MetricsInterval);
                Services.AddSingleton<MetricsCollector>(metricsCollector);
            }
            catch (Exception ex)
            {
                L.LogError(ex, "Failed to configure monitoring services");
            }
        }

        /// <summary>
        /// Initialize components from service collection
        /// </summary>
        protected virtual void InitializeComponents()
        {
            // Build the service provider
            var serviceProvider = Services.BuildServiceProvider();
            
            // Get monitoring components
            HealthMonitor = serviceProvider.GetService<HealthMonitor>();
            Metrics = serviceProvider.GetService<MetricsCollector>();
            
            L.LogDebug("Service components initialized");
        }

        /// <summary>
        /// Run the service with periodic tick events - starts the service and waits for completion
        /// </summary>
        /// <param name="args">Command line arguments</param>
        public override async Task RunAsync(IEnumerable<string> args)
        {
            await _serviceLock.WaitAsync();
            try
            {
                L.LogInfo($"Starting service: {Config.App.Name}");
                
                // Start health monitor if available
                if (HealthMonitor != null)
                {
                    await HealthMonitor.StartAsync();
                }
                
                // Run in service mode - continue until stopped
                var tcs = new TaskCompletionSource<bool>();
                
                // Set up termination handling
                Console.CancelKeyPress += (s, e) => {
                    e.Cancel = true;
                    tcs.TrySetResult(true);
                };
                
                try
                {
                    // Wait for termination signal
                    await tcs.Task;
                }
                catch (Exception ex)
                {
                    L.LogError(ex, "Error in service main loop");
                    throw;
                }
            }
            finally
            {
                _serviceLock.Release();
            }
        }

        /// <summary>
        /// Tick event for periodic processing (inherited from GhostApp)
        /// </summary>
        protected override async Task OnTickAsync()
        {
            // Get process metrics
            var process = System.Diagnostics.Process.GetCurrentProcess();
            
            // Basic health check
            bool isHealthy = !process.HasExited && process.Responding;
            
            // Update health status
            HealthStatus newStatus = isHealthy ? HealthStatus.Healthy : HealthStatus.Degraded;
            
            // Report health change if needed
            if (newStatus != HealthStatus)
            {
                HealthStatus = newStatus;
                
                if (HealthMonitor != null)
                {
                    await HealthMonitor.ReportHealthAsync(new HealthReport(
                        Status: HealthStatus,
                        Message: isHealthy ? "Service is healthy" : "Service is degraded",
                        Metrics: new Dictionary<string, object>
                        {
                            ["memory"] = process.WorkingSet64,
                            ["cpu"] = process.TotalProcessorTime.TotalSeconds,
                            ["threads"] = process.Threads.Count
                        },
                        Timestamp: DateTime.UtcNow
                    ));
                }
            }
            
            // Run custom service tick logic
            await ServiceTickAsync();
        }
        
        /// <summary>
        /// Service-specific tick processing - override in derived classes
        /// </summary>
        protected virtual Task ServiceTickAsync()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Clean up resources
        /// </summary>
        public override async ValueTask DisposeAsync()
        {
            try
            {
                // Dispose health monitor
                if (HealthMonitor != null && HealthMonitor is IAsyncDisposable healthDisposable)
                {
                    await healthDisposable.DisposeAsync();
                }
                
                // Dispose metrics collector
                if (Metrics != null && Metrics is IAsyncDisposable metricsDisposable)
                {
                    await metricsDisposable.DisposeAsync();
                }
                
                // Dispose service collection if needed
                if (Services.BuildServiceProvider() is IAsyncDisposable servicesDisposable)
                {
                    await servicesDisposable.DisposeAsync();
                }
                
                // Dispose lock
                _serviceLock.Dispose();
                
                // Let base class handle further cleanup
                await base.DisposeAsync();
            }
            catch (Exception ex)
            {
                L.LogError(ex, "Error disposing service");
            }
        }
    }
}