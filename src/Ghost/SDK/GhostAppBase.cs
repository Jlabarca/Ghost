// Ghost/SDK/GhostAppBase.cs
using Ghost.Core.Config;
using System.Threading;
using System.Threading.Tasks;
using Ghost.Core.Data;
using Ghost.Core.Storage;
using Ghost.Core.Monitoring;
using Ghost.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghost.SDK
{
    /// <summary>
    /// Core base class that handles all the infrastructure
    /// Think of this as the "building foundation" - all Ghost apps are built on top of this
    /// </summary>
    public abstract class GhostAppBase : IAsyncDisposable
    {
        protected readonly IRedisManager Bus;
        protected readonly IDataAPI Data;
        protected readonly IConfigManager Config;
        protected readonly IAutoMonitor Metrics;
        protected readonly GhostLogger Logger;

        private readonly CancellationTokenSource _cts;
        private bool _isInitialized;
        private bool _isDisposed;

        protected GhostAppBase(GhostOptions options = null)
        {
            options ??= new GhostOptions();

            Bus = new RedisManager(options.UseRedis ?
                    new RedisClient(options.RedisConnectionString) :
                    new LocalCacheClient(options.DataDirectory));

            Data = new DataAPI(
                    new StorageRouter(
                            options.UseRedis ?
                                    new RedisClient(options.RedisConnectionString) :
                                    new LocalCacheClient(options.DataDirectory),
                            options.UsePostgres ?
                                    new PostgresClient(options.PostgresConnectionString) :
                                    new SQLiteClient(Path.Combine(options.DataDirectory, "ghost.db")),
                            new PermissionsManager(
                                    options.UsePostgres ?
                                            new PostgresClient(options.PostgresConnectionString) :
                                            new SQLiteClient(Path.Combine(options.DataDirectory, "ghost.db")),
                                    options.UseRedis ?
                                            new RedisClient(options.RedisConnectionString) :
                                            new LocalCacheClient(options.DataDirectory)
                            )
                    ),
                    options.UseRedis ?
                            new RedisClient(options.RedisConnectionString) :
                            new LocalCacheClient(options.DataDirectory)
            );

            Config = new ConfigManager(
                    options.UseRedis ?
                            new RedisClient(options.RedisConnectionString) :
                            new LocalCacheClient(options.DataDirectory),
                    new StorageRouter(
                            options.UseRedis ?
                                    new RedisClient(options.RedisConnectionString) :
                                    new LocalCacheClient(options.DataDirectory),
                            options.UsePostgres ?
                                    new PostgresClient(options.PostgresConnectionString) :
                                    new SQLiteClient(Path.Combine(options.DataDirectory, "ghost.db")),
                            new PermissionsManager(
                                    options.UsePostgres ?
                                            new PostgresClient(options.PostgresConnectionString) :
                                            new SQLiteClient(Path.Combine(options.DataDirectory, "ghost.db")),
                                    options.UseRedis ?
                                            new RedisClient(options.RedisConnectionString) :
                                            new LocalCacheClient(options.DataDirectory)
                            )
                    )
            );

            Metrics = new AutoMonitor(Bus);
            Logger = new GhostLogger(
                    options.UseRedis ?
                            new RedisClient(options.RedisConnectionString) :
                            new LocalCacheClient(options.DataDirectory),
                    new GhostLoggerConfiguration
                    {
                            RedisKeyPrefix = $"ghost:logs:{options.SystemId}",
                            LogsPath = Path.Combine(options.DataDirectory, "logs"),
                            OutputsPath = Path.Combine(options.DataDirectory, "outputs")
                    }
            );

            _cts = new CancellationTokenSource();
        }

        protected virtual async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                // Start monitoring if enabled
                if (Metrics != null)
                {
                    await Metrics.StartAsync();
                }

                _isInitialized = true;

                Logger.Log("Application initialized successfully", LogLevel.Information);
            }
            catch (Exception ex)
            {
                Logger.Log("Failed to initialize application", LogLevel.Error, ex);
                throw;
            }
        }

        protected virtual async Task ShutdownAsync()
        {
            if (!_isInitialized) return;

            try
            {
                // Stop monitoring
                if (Metrics != null)
                {
                    await Metrics.StopAsync();
                }

                _isInitialized = false;

                Logger.Log("Application shut down successfully", LogLevel.Information);
            }
            catch (Exception ex)
            {
                Logger.Log("Error during shutdown", LogLevel.Error, ex);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;

            try
            {
                _cts.Cancel();
                await ShutdownAsync();

                // Dispose all resources
                if (Bus is IAsyncDisposable busDisposable)
                    await busDisposable.DisposeAsync();
                if (Data is IAsyncDisposable dataDisposable)
                    await dataDisposable.DisposeAsync();
                if (Config is IAsyncDisposable configDisposable)
                    await configDisposable.DisposeAsync();
                if (Metrics is IAsyncDisposable metricsDisposable)
                    await metricsDisposable.DisposeAsync();

                _cts.Dispose();
                _isDisposed = true;
            }
            catch (Exception ex)
            {
                Logger.Log("Error during disposal", LogLevel.Error, ex);
                throw;
            }
        }

        protected CancellationToken CancellationToken => _cts.Token;
    }

// Ghost/SDK/GhostApp.cs
}

// Ghost/SDK/GhostServiceApp.cs

/// <summary>
/// Base class for long-running Ghost service apps
/// Think of this as a "daemon" - it runs continuously until stopped
/// </summary>
// public abstract class GhostServiceApp : GhostAppBase
// {
//     private Task _executionTask;
//     private readonly TaskCompletionSource _startCompletionSource;
//     private readonly TaskCompletionSource _stopCompletionSource;
//
//     private volatile bool _isStarted;
//     private volatile bool _isStopping;
//
//     protected GhostServiceApp(GhostOptions options = null) : base(options)
//     {
//         _startCompletionSource = new TaskCompletionSource();
//         _stopCompletionSource = new TaskCompletionSource();
//     }
//
//     /// <summary>
//     /// Main execution loop for the service. Override this to implement your service's logic.
//     /// </summary>
//     protected abstract Task ExecuteAsync(CancellationToken cancellationToken);
//
//     /// <summary>
//     /// Starts the service
//     /// </summary>
//     public async Task StartAsync()
//     {
//         if (_isStarted)
//         {
//             throw new InvalidOperationException("Service is already started");
//         }
//
//         try
//         {
//             await InitializeAsync();
//
//             _executionTask = ExecuteAsync(CancellationToken);
//             _isStarted = true;
//
//             Logger.Log("Service started successfully", LogLevel.Information);
//             _startCompletionSource.SetResult();
//         }
//         catch (Exception ex)
//         {
//             Logger.Log("Failed to start service", LogLevel.Error, ex);
//             _startCompletionSource.SetException(ex);
//             throw;
//         }
//     }
//
//     /// <summary>
//     /// Stops the service
//     /// </summary>
//     public async Task StopAsync()
//     {
//         if (!_isStarted || _isStopping)
//         {
//             return;
//         }
//
//         try
//         {
//             _isStopping = true;
//             Logger.Log("Stopping service...", LogLevel.Information);
//
//             _cts?.Cancel();
//
//             if (_executionTask != null)
//             {
//                 try
//                 {
//                     await _executionTask;
//                 }
//                 catch (OperationCanceledException)
//                 {
//                     // Normal cancellation, ignore
//                 }
//             }
//
//             await ShutdownAsync();
//
//             Logger.Log("Service stopped successfully", LogLevel.Information);
//             _stopCompletionSource.SetResult();
//         }
//         catch (Exception ex)
//         {
//             Logger.Log("Error stopping service", LogLevel.Error, ex);
//             _stopCompletionSource.SetException(ex);
//             throw;
//         }
//         finally
//         {
//             _isStarted = false;
//             _isStopping = false;
//         }
//     }
//
//     /// <summary>
//     /// Runs a Ghost service of the specified type
//     /// </summary>
//     public static async Task RunAsync<T>(GhostOptions options = null) where T : GhostServiceApp
//     {
//         await using var service = (T)Activator.CreateInstance(typeof(T), options);
//         var cts = new CancellationTokenSource();
//
//         Console.CancelKeyPress += (s, e) =>
//         {
//             e.Cancel = true;
//             cts.Cancel();
//         };
//
//         try
//         {
//             await service.StartAsync();
//             await Task.Delay(-1, cts.Token);
//         }
//         catch (OperationCanceledException)
//         {
//             // Normal shutdown
//         }
//         finally
//         {
//             await service.StopAsync();
//         }
//     }
//
//     /// <summary>
//     /// Waits for the service to start
//     /// </summary>
//     public Task WaitForStartAsync() => _startCompletionSource.Task;
//
//     /// <summary>
//     /// Waits for the service to stop
//     /// </summary>
//     public Task WaitForStopAsync() => _stopCompletionSource.Task;
// }