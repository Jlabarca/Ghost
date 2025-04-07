// using System;
// using System.Collections.Generic;
// using System.Threading;
// using System.Threading.Tasks;
// using Ghost.Core.Config;
// using Ghost.Core.Storage;
// using Ghost.Core.Data;
// using Ghost.Core.Monitoring;
// using Microsoft.Extensions.DependencyInjection;
//
// namespace Ghost.SDK
// {
//     /// <summary>
//     /// Base class for Ghost service applications with long-running processes
//     /// </summary>
//     public class GhostServiceApp : GhostApp
//     {
//         private readonly SemaphoreSlim _serviceLock = new SemaphoreSlim(1, 1);
//         protected CancellationTokenSource _cts = new CancellationTokenSource();
//         protected Timer _tickTimer;
//         protected int _restartAttempts = 0;
//         protected DateTime? _lastRestartTime;
//
//         /// <summary>
//         /// Service collection for dependency injection
//         /// </summary>
//         protected ServiceCollection Services { get; } = new ServiceCollection();
//
//         /// <summary>
//         /// Bus for inter-process messaging
//         /// </summary>
//         protected IGhostBus Bus { get; private set; }
//
//         /// <summary>
//         /// Data access for persistent storage
//         /// </summary>
//         protected IGhostData Data { get; private set; }
//
//         /// <summary>
//         /// Health monitor for service state
//         /// </summary>
//         protected HealthMonitor HealthMonitor { get; private set; }
//
//         /// <summary>
//         /// Metrics collector for performance tracking
//         /// </summary>
//         protected MetricsCollector Metrics { get; private set; }
//
//         /// <summary>
//         /// Initialize a new service app
//         /// </summary>
//         /// <param name="config">Optional configuration override</param>
//         public GhostServiceApp(GhostConfig config = null) : base(config)
//         {
//             // Service apps are long-running by default
//             IsService = true;
//
//             // Configure services
//             ConfigureServices();
//
//             // Initialize components
//             try
//             {
//                 InitializeComponents();
//             }
//             catch (Exception ex)
//             {
//                 G.LogError(ex, "Failed to initialize service components");
//             }
//         }
//
//         /// <summary>
//         /// Configure dependency injection services
//         /// </summary>
//         protected virtual void ConfigureServices()
//         {
//             // Add configuration
//             Services.AddSingleton(Config);
//
//             // Configure dependencies based on whether auto-monitoring is enabled
//             if (AutoGhostFather && AutoMonitor)
//             {
//                 ConfigureMonitoringServices();
//             }
//         }
//
//         /// <summary>
//         /// Configure monitoring-related services
//         /// </summary>
//         protected virtual void ConfigureMonitoringServices()
//         {
//             try
//             {
//                 // Configure cache
//                 var cachePath = Config.Core.DataPath ?? "data/cache";
//                 System.IO.Directory.CreateDirectory(cachePath);
//                 var cache = new LocalCache(cachePath);
//                 Services.AddSingleton<ICache>(cache);
//
//                 // Configure bus
//                 var bus = new GhostBus(cache);
//                 Services.AddSingleton<IGhostBus>(bus);
//
//                 // Configure health monitor
//                 var healthMonitor = new HealthMonitor(
//                     checkInterval: Config.Core.HealthCheckInterval,
//                     bus: bus);
//                 Services.AddSingleton<HealthMonitor>(healthMonitor);
//
//                 // Configure metrics collector
//                 var metricsCollector = new MetricsCollector(
//                     interval: Config.Core.MetricsInterval,
//                     data: database);
//                 Services.AddSingleton<MetricsCollector>(metricsCollector);
//             }
//             catch (Exception ex)
//             {
//                 G.LogError(ex, "Failed to configure monitoring services");
//             }
//         }
//
//         /// <summary>
//         /// Initialize components from service collection
//         /// </summary>
//         protected virtual void InitializeComponents()
//         {
//             // Build the service provider
//             var serviceProvider = Services.BuildServiceProvider();
//
//             // Get required components
//             Bus = serviceProvider.GetService<IGhostBus>();
//             Data = serviceProvider.GetService<IGhostData>();
//             HealthMonitor = serviceProvider.GetService<HealthMonitor>();
//             Metrics = serviceProvider.GetService<MetricsCollector>();
//
//             G.LogDebug("Service components initialized");
//         }
//
//         /// <summary>
//         /// Run the service with periodic tick events
//         /// </summary>
//         /// <param name="args">Command line arguments</param>
//         public override async Task RunAsync(IEnumerable<string> args)
//         {
//             await _serviceLock.WaitAsync();
//             try
//             {
//                 G.LogInfo($"Starting service: {Config.App.Name}");
//
//                 // Report starting state
//                 if (AutoGhostFather && AutoMonitor && Connection != null)
//                 {
//                     await Connection.ReportHealthAsync("Starting", "Service is starting");
//                 }
//
//                 // Start health monitor if available
//                 if (HealthMonitor != null)
//                 {
//                     await HealthMonitor.StartAsync(_cts.Token);
//                 }
//
//                 // Start metrics collector if available
//                 if (Metrics != null)
//                 {
//                     await Metrics.StartAsync();
//                 }
//
//                 // Start tick timer for periodic processing
//                 if (TickInterval > TimeSpan.Zero)
//                 {
//                     _tickTimer = new Timer(
//                         callback: OnTickCallback,
//                         state: null,
//                         dueTime: TimeSpan.Zero,
//                         period: TickInterval);
//                 }
//
//                 // Report running state
//                 if (AutoGhostFather && AutoMonitor && Connection != null)
//                 {
//                     await Connection.ReportHealthAsync("Running", "Service is running");
//                 }
//             }
//             finally
//             {
//                 _serviceLock.Release();
//             }
//         }
//
//         /// <summary>
//         /// Execute the periodic tick callback
//         /// </summary>
//         private async void OnTickCallback(object state)
//         {
//             if (_cts.IsCancellationRequested) return;
//
//             try
//             {
//                 await OnTickAsync();
//
//                 // Report heartbeat
//                 if (AutoGhostFather && AutoMonitor && Connection != null)
//                 {
//                     await Connection.ReportMetricsAsync();
//                 }
//             }
//             catch (Exception ex)
//             {
//                 G.LogError(ex, "Error in service tick callback");
//
//                 if (AutoRestart && (_restartAttempts < MaxRestartAttempts || MaxRestartAttempts == 0))
//                 {
//                     // Handle restarts with exponential backoff
//                     var now = DateTime.UtcNow;
//                     if (!_lastRestartTime.HasValue || (now - _lastRestartTime.Value).TotalMinutes > 5)
//                     {
//                         // Reset counter after 5 minutes of stability
//                         _restartAttempts = 0;
//                     }
//
//                     _restartAttempts++;
//                     _lastRestartTime = now;
//
//                     G.LogWarn($"Service tick failed. Attempting restart ({_restartAttempts}/{MaxRestartAttempts})");
//
//                     // Calculate backoff delay (exponential with jitter)
//                     var backoffSeconds = Math.Min(30, Math.Pow(2, _restartAttempts - 1));
//                     var jitter = new Random().NextDouble() * 0.5 + 0.75; // 75-125% of base delay
//                     var delayMs = (int)(backoffSeconds * 1000 * jitter);
//
//                     // Report restarting status
//                     if (AutoGhostFather && AutoMonitor && Connection != null)
//                     {
//                         await Connection.ReportHealthAsync("Restarting", $"Service is restarting after error: {ex.Message}");
//                     }
//
//                     // Restart service after delay
//                     await Task.Delay(delayMs);
//
//                     // Resume normal operation
//                     if (AutoGhostFather && AutoMonitor && Connection != null)
//                     {
//                         await Connection.ReportHealthAsync("Running", "Service has recovered from error");
//                     }
//                 }
//                 else if (AutoRestart && MaxRestartAttempts > 0 && _restartAttempts >= MaxRestartAttempts)
//                 {
//                     G.LogError($"Service failed after {_restartAttempts} restart attempts. Giving up.");
//
//                     // Report failed status
//                     if (AutoGhostFather && AutoMonitor && Connection != null)
//                     {
//                         await Connection.ReportHealthAsync("Failed", $"Service failed after {_restartAttempts} restart attempts");
//                     }
//
//                     // Stop the service
//                     await StopAsync();
//                 }
//             }
//         }
//
//         /// <summary>
//         /// Stop the service and cleanup resources
//         /// </summary>
//         public override async Task StopAsync()
//         {
//             await _serviceLock.WaitAsync();
//             try
//             {
//                 G.LogInfo($"Stopping service: {Config.App.Name}");
//
//                 // Report stopping state
//                 if (AutoGhostFather && AutoMonitor && Connection != null)
//                 {
//                     await Connection.ReportHealthAsync("Stopping", "Service is shutting down");
//                 }
//
//                 // Cancel any pending operations
//                 _cts.Cancel();
//
//                 // Stop tick timer
//                 if (_tickTimer != null)
//                 {
//                     await _tickTimer.DisposeAsync();
//                     _tickTimer = null;
//                 }
//
//                 // Stop metrics collector
//                 if (Metrics != null)
//                 {
//                     await Metrics.StopAsync();
//                 }
//
//                 // Report stopped state
//                 if (AutoGhostFather && AutoMonitor && Connection != null)
//                 {
//                     await Connection.ReportHealthAsync("Stopped", "Service has shut down");
//                 }
//
//                 // Let base class handle the rest
//                 await base.StopAsync();
//             }
//             finally
//             {
//                 _serviceLock.Release();
//             }
//         }
//
//         /// <summary>
//         /// Tick event for periodic processing
//         /// </summary>
//         protected virtual Task OnTickAsync()
//         {
//             return Task.CompletedTask;
//         }
//
//         /// <summary>
//         /// Clean up resources
//         /// </summary>
//         public override async ValueTask DisposeAsync()
//         {
//             try
//             {
//                 // Stop the service if it's running
//                 await StopAsync();
//
//                 // Dispose service components
//                 if (Metrics != null && Metrics is IAsyncDisposable metricsDisposable)
//                 {
//                     await metricsDisposable.DisposeAsync();
//                 }
//
//                 if (HealthMonitor != null && HealthMonitor is IAsyncDisposable healthDisposable)
//                 {
//                     await healthDisposable.DisposeAsync();
//                 }
//
//                 if (Data != null && Data is IAsyncDisposable dataDisposable)
//                 {
//                     await dataDisposable.DisposeAsync();
//                 }
//
//                 if (Bus != null && Bus is IAsyncDisposable busDisposable)
//                 {
//                     await busDisposable.DisposeAsync();
//                 }
//
//                 // Dispose service collection if it contains disposable objects
//                 if (Services is ServiceProvider serviceProvider)
//                 {
//                     await serviceProvider.DisposeAsync();
//                 }
//
//                 // Let base class handle the rest
//                 await base.DisposeAsync();
//
//                 // Clean up synchronization primitive
//                 _serviceLock.Dispose();
//                 _cts.Dispose();
//             }
//             catch (Exception ex)
//             {
//                 G.LogError(ex, "Error disposing service");
//             }
//         }
//     }
// }