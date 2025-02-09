using Ghost.Core.Config;
using Ghost.Core.Storage;
using Ghost.Core.Storage.Cache;
using Ghost.Core.Storage.Database;
using Ghost.Father;
using Ghost.Infrastructure.Logging;

namespace Ghost.SDK;

/// <summary>
/// Core base class that handles all the infrastructure.
/// Think of this as the "building foundation" - all Ghost apps are built on top of this.
/// </summary>
public abstract class GhostAppBase : IAsyncDisposable
{
    internal protected readonly IGhostBus Bus;
    internal protected readonly IGhostData Data;
    internal protected readonly IGhostConfig Config;
    internal protected readonly IAutoMonitor Metrics;

    protected readonly CancellationTokenSource _cts;
    private bool _isInitialized;
    private bool _isDisposed;

    protected GhostAppBase(GhostOptions options = null)
    {
        options ??= new GhostOptions();
        _cts = new CancellationTokenSource();

        // Setup cache
        ICache cache = options.UseRedis
            ? new RedisCache(options.RedisConnectionString)
            : new LocalCache(options.DataDirectory);

        // Setup database
        IDatabaseClient db = options.UsePostgres
            ? new PostgresClient(options.PostgresConnectionString)
            : new SQLiteClient(Path.Combine(options.DataDirectory, "ghost.db"));

        // Setup core services
        Data = new GhostData(db, cache);
        Bus = new GhostBus(cache);
        Config = new GhostConfig(options);
        Metrics = new AutoMonitor(Bus);

        G.LogInfo("GhostAppBase constructed with options: UseRedis={0}, UsePostgres={1}",
            options.UseRedis, options.UsePostgres);
    }

    protected async virtual Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            // Ensure database schema exists
            if (Data is ISchemaInitializer schema)
            {
                await schema.InitializeSchemaAsync();
            }

            // Start monitoring
            if (Metrics != null)
            {
                await Metrics.StartAsync();
            }

            // Load initial configuration
            if (Config is IConfigInitializer config)
            {
                await config.LoadConfigurationAsync();
            }

            _isInitialized = true;
            G.LogInfo("Application initialized successfully");
        }
        catch (Exception ex)
        {
            G.LogError("Failed to initialize application", ex);
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

            // Persist any pending state
            if (Config is IConfigPersister config)
            {
                await config.PersistConfigurationAsync();
            }

            _isInitialized = false;
            G.LogInfo("Application shut down successfully");
        }
        catch (Exception ex)
        {
            G.LogError("Error during shutdown", ex);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        try
        {
            _cts.Cancel();

            // Cleanup in order
            await ShutdownAsync();

            if (Metrics is IAsyncDisposable metricsDisposable)
                await metricsDisposable.DisposeAsync();

            if (Bus is IAsyncDisposable busDisposable)
                await busDisposable.DisposeAsync();

            if (Data is IAsyncDisposable dataDisposable)
                await dataDisposable.DisposeAsync();

            if (Config is IAsyncDisposable configDisposable)
                await configDisposable.DisposeAsync();

            _cts.Dispose();
            _isDisposed = true;

            G.LogInfo("Application disposed successfully");
        }
        catch (Exception ex)
        {
            G.LogError("Error during disposal", ex);
            throw;
        }
    }

    protected CancellationToken CancellationToken => _cts.Token;
}

/// <summary>
/// Interface for database schema initialization
/// </summary>
public interface ISchemaInitializer
{
    Task InitializeSchemaAsync();
}

/// <summary>
/// Interface for configuration initialization
/// </summary>
public interface IConfigInitializer
{
    Task LoadConfigurationAsync();
}

/// <summary>
/// Interface for configuration persistence
/// </summary>
public interface IConfigPersister
{
    Task PersistConfigurationAsync();
}