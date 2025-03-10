using Ghost;
using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Logging;
using Ghost.Core.Modules;
using Ghost.Core.Storage;
using Ghost.SDK;
using Microsoft.Extensions.DependencyInjection;
public abstract class GhostAppBase : IAsyncDisposable
{
    internal protected readonly IGhostBus Bus;
    internal protected readonly IGhostData Data;
    internal protected readonly IAutoMonitor Metrics;
    internal protected readonly ServiceCollection Services;
    internal protected readonly GhostConfig Config;
    protected readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isInitialized;
    private bool _disposed;

    protected GhostAppBase(GhostConfig? config = null)
    {
        _cts = new CancellationTokenSource();
        _isInitialized = false;
        _disposed = false;

        // Create default config if none provided
        Config = config ?? new GhostConfig
        {
            App = new AppInfo
            {
                Id = "ghost",
                Name = "Ghost",
                Description = "Ghost Process Manager",
                Version = "1.0.0"
            },
            Core = new CoreConfig
            {
                HealthCheckInterval = TimeSpan.FromSeconds(30),
                MetricsInterval = TimeSpan.FromSeconds(5)
            },
            Modules = new Dictionary<string, ModuleConfig>()
        };

        // Create service collection
        // GhostApp core services TODO: arrange core
        // Build service provider


        Services = new ServiceCollection();

        // Configure services
        ConfigureServices(Services);

        var provider = Services.BuildServiceProvider();

        // Resolve dependencies
        Bus = provider.GetRequiredService<IGhostBus>();
        Data = provider.GetRequiredService<IGhostData>();
        Metrics = provider.GetRequiredService<IAutoMonitor>();

        Services.AddSingleton(Config);
        Services.AddSingleton(Bus);
        Services.AddSingleton(Data);
        Services.AddSingleton(Metrics);



        G.LogInfo("GhostAppBase constructed with config: {0}", Config.App.Id);
    }

    protected async Task InitializeAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostAppBase));
        if (_isInitialized) return;

        await _lock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            // Initialize core services
            await Data.InitializeAsync();

            // Start monitoring if enabled
            if (Metrics != null && Config.Core.MetricsInterval > TimeSpan.Zero)
            {
                G.LogInfo("Starting metrics collection...");
                await Metrics.StartAsync();
            }

            _isInitialized = true;
            G.LogInfo("Application initialized successfully");
        }
        catch (Exception ex)
        {
            G.LogError("Failed to initialize application", ex);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    protected async Task ShutdownAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostAppBase));
        if (!_isInitialized) return;

        await _lock.WaitAsync();
        try
        {
            if (!_isInitialized) return;

            // Stop monitoring
            if (Metrics != null)
            {
                G.LogInfo("Stopping metrics collection...");
                await Metrics.StopAsync();
            }

            // Cleanup any active bus subscriptions
            if (Bus != null)
            {
                G.LogInfo("Cleaning up message bus...");
                await Bus.UnsubscribeAsync("*");
            }

            _isInitialized = false;
            G.LogInfo("Application shut down successfully");
        }
        catch (Exception ex)
        {
            G.LogError("Error during shutdown", ex);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostAppBase));

        // Register config
        services.AddSingleton(Config);

        // Cache setup
        // ICache cache = Config.HasModule("redis")
        //     ? new RedisCache(Config.GetModuleConfig<RedisConfig>("redis").ConnectionString)
        //     : new LocalCache(Config.GetModuleConfig<LocalCacheConfig>("cache")?.Path ?? "cache");

        ICache cache = new LocalCache(Config.GetModuleConfig<LocalCacheConfig>("cache")?.Path ?? "cache");
        services.AddSingleton(cache);

        // Configure logging
        services.AddGhostLogger(cache, config => {
            var loggingConfig = Config.GetModuleConfig<LoggingConfig>("logging");
            config.RedisKeyPrefix = $"ghost:logs:{Config.App.Id}";
            config.LogsPath = loggingConfig?.LogsPath ?? "logs";
            config.OutputsPath = loggingConfig?.OutputsPath ?? "outputs";
        });

        // Database client
        services.AddSingleton<IDatabaseClient>(sp => {
            // if (Config.HasModule("postgres"))
            // {
            //     var pgConfig = Config.GetModuleConfig<PostgresConfig>("postgres");
            //     return new PostgresDatabase(pgConfig.ConnectionString);
            // }

            var dbPath = Path.Combine(
                Config.GetModuleConfig<LocalCacheConfig>("cache")?.Path ?? "data",
                "ghost.db"
            );
            return new SQLiteDatabase(dbPath);
        });

        // Core services
        services.AddSingleton<IGhostData>(sp => {
            var db = sp.GetRequiredService<IDatabaseClient>();
            var kvStore = new SQLiteKeyValueStore(db);
            var schema = db.DatabaseType == DatabaseType.PostgreSQL
                ? new PostgresSchemaManager(db)
                : new SQLiteSchemaManager(db) as ISchemaManager;
            return new GhostData(db, kvStore, cache, schema);
        });

        services.AddSingleton<IGhostBus>(sp => new GhostBus(cache));

        services.AddSingleton<IAutoMonitor>(sp => {
            var bus = sp.GetRequiredService<IGhostBus>();
            return new AutoMonitor(bus, Config.App.Id, Config.App.Name);
        });

        // Allow derived classes to add their services
        ConfigureAppServices(services);
    }

    protected virtual void ConfigureAppServices(IServiceCollection services)
    {

    }

    public async virtual ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();

            if (Metrics is IAsyncDisposable metricsDisposable)
                await metricsDisposable.DisposeAsync();

            if (Bus is IAsyncDisposable busDisposable)
                await busDisposable.DisposeAsync();

            if (Data is IAsyncDisposable dataDisposable)
                await dataDisposable.DisposeAsync();

            if (Services is IAsyncDisposable servicesDisposable)
                await servicesDisposable.DisposeAsync();

            _cts.Dispose();
            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }
}
