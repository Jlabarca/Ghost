using Ghost.Config;
using Ghost.Data;
using Ghost.Data.Decorators;
using Ghost.Data.Implementations;
using Ghost.Logging;
using Ghost.Monitoring;
using Ghost.Pooling;
using Ghost.Storage;
using Ghost.Testing.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Ghost;

public abstract partial class GhostApp
{
#region Dependency Injection and Service Configuration

    /// <summary>
    ///     Base service configuration that initializes all essential services:
    ///     Logger, Cache, Bus, Database in the correct order
    /// </summary>
    private IServiceProvider ConfigureServicesBase()
    {
        ServiceCollection services = new ServiceCollection();

        // 1. FOUNDATION - Register core configuration and logger first
        services.AddSingleton(Config);
        services.AddSingleton<IServiceCollection>(services);

        services.AddSingleton(G.GetLogger());
        //services.AddLogging(builder => builder.AddConsole());

        // 2. CACHE - Set up caching layer (needed by Bus and Data)
        ConfigureCacheServices(services);

        // 3. BUS - Set up messaging (depends on Cache)
        ConfigureBusServices(services);

        // 4. DATABASE - Set up data layer (the most complex part)
        ConfigureDatabaseServices(services);

        // 5. MONITORING - Set up metrics and monitoring
        ConfigureMonitoringServices(services);

        // 6. APPLICATION SPECIFIC - Allow derived apps to add their services
        ConfigureServices(services);

        // Build and register cleanup
        ServiceProvider serviceProvider = services.BuildServiceProvider();
        RegisterDisposalAction(async () =>
        {
            if (serviceProvider is IAsyncDisposable ad)
            {
                await ad.DisposeAsync();
            }
            else if (serviceProvider is IDisposable d)
            {
                d.Dispose();
            }
        });

        return serviceProvider;
    }

#region Cache Configuration

    private void ConfigureCacheServices(IServiceCollection services)
    {
        services.AddSingleton<ICache>(provider =>
        {
            GhostConfig config = provider.GetRequiredService<GhostConfig>();
            IGhostLogger logger = provider.GetRequiredService<IGhostLogger>();

            // Use Redis if enabled and configured
            if (config.Redis.Enabled && !string.IsNullOrEmpty(config.Redis.ConnectionString))
            {
                try
                {
                    G.LogInfo($"Creating RedisCache with connection: {config.Redis.ConnectionString.Split(';')[0]}...");
                    return new RedisCache(config.Redis.ConnectionString, logger);
                }
                catch (Exception ex)
                {
                    G.LogError(ex, "Failed to create RedisCache. Falling back to MemoryCache.");
                }
            }

            G.LogInfo("Using MemoryCache for caching.");
            return new MemoryCache(logger);
        });
    }

#endregion

#region Bus Configuration

    private void ConfigureBusServices(IServiceCollection services)
    {
        services.AddSingleton<IGhostBus>(provider =>
        {
            GhostConfig config = provider.GetRequiredService<GhostConfig>();
            ICache cache = provider.GetRequiredService<ICache>();

            // Use Redis for bus if enabled and configured
            if (config.Redis.Enabled && !string.IsNullOrEmpty(config.Redis.ConnectionString))
            {
                try
                {
                    G.LogInfo($"Creating RedisGhostBus with connection: {config.Redis.ConnectionString.Split(';')[0]}...");
                    RedisGhostBus redisBus = new RedisGhostBus(config.Redis.ConnectionString);

                    // Test connection in background
                    _ = Task.Run(async () =>
                    {
                        bool available = await redisBus.IsAvailableAsync();
                        G.LogInfo($"RedisGhostBus availability: {available}");
                    });

                    return redisBus;
                }
                catch (Exception ex)
                {
                    G.LogError(ex, "Failed to create RedisGhostBus. Falling back to in-memory bus.");
                }
            }

            G.LogInfo("Using in-memory GhostBus for messaging.");
            return new GhostBus(cache);
        });
    }

#endregion

#region Database Configuration

    private void ConfigureDatabaseServices(IServiceCollection services)
    {
        // Connection pooling
        services.AddSingleton<ConnectionPoolManager>();

        // Schema management
        services.AddSingleton<ISchemaManager, PostgresSchemaManager>();

        // Database client
        services.AddSingleton<IDatabaseClient>(provider =>
        {
            GhostConfig config = provider.GetRequiredService<GhostConfig>();
            IGhostLogger logger = G.GetLogger();

            // Check if we should use in-memory for testing
            if (config.Core.UseInMemoryDatabase.GetValueOrDefault())
            {
                G.LogInfo("Using InMemoryDatabaseClient for testing mode.");
                InMemoryGhostData inMemoryData = provider.GetRequiredService<InMemoryGhostData>();
                return new InMemoryDatabaseClient(inMemoryData, logger);
            }

            // Use PostgreSQL by default
            if (config.Postgres.Enabled && !string.IsNullOrEmpty(config.Postgres.ConnectionString))
            {
                G.LogInfo("Using PostgreSqlClient for database operations.");
                return new PostgreSqlClient(config.Postgres.ConnectionString);
            }

            throw new InvalidOperationException("No valid database configuration found. Either enable UseInMemoryDatabase or configure PostgreSQL.");
        });

        // Register InMemoryGhostData if needed for testing
        if (Config.Core.UseInMemoryDatabase.GetValueOrDefault())
        {
            services.AddSingleton<InMemoryGhostData>();
        }

        // Core data implementation
        services.AddSingleton<CoreGhostData>();

        // Build the decorated IGhostData chain
        services.AddSingleton<IGhostData>(provider =>
        {
            GhostConfig config = provider.GetRequiredService<GhostConfig>();

            // Start with core implementation
            IGhostData dataLayer = provider.GetRequiredService<CoreGhostData>();

            // Apply decorators based on configuration
            dataLayer = ApplyDataLayerDecorators(dataLayer, config, provider);

            G.LogInfo("IGhostData configured with decorators based on application settings.");
            return dataLayer;
        });
    }

    private IGhostData ApplyDataLayerDecorators(IGhostData core, GhostConfig config, IServiceProvider provider)
    {
        IGhostData current = core;

        // 1. Encryption (innermost - closest to data)
        if (config.Security.EnableEncryption.GetValueOrDefault())
        {
            var securityOptions = Options.Create(config.Security);
            current = new EncryptedGhostData(current, securityOptions, G.GetLogger());
            G.LogInfo("Applied encryption decorator to data layer.");
        }

        // 2. Caching
        if (config.Caching.UseL1Cache.GetValueOrDefault())
        {
            ICache cache = provider.GetRequiredService<ICache>();
            var cachingOptions = Options.Create(config.Caching);
            current = new CachedGhostData(current, cache, cachingOptions, G.GetLogger());
            G.LogInfo("Applied caching decorator to data layer.");
        }

        // 3. Resilience (retry, circuit breaker)
        if (config.Resilience.EnableRetry.GetValueOrDefault()
            || config.Resilience.EnableCircuitBreaker.GetValueOrDefault())
        {
            var resilienceOptions = Options.Create(config.Resilience);
            current = new ResilientGhostData(current, G.GetLogger(), resilienceOptions);
            G.LogInfo("Applied resilience decorator to data layer.");
        }

        // 4. Instrumentation (outermost - metrics and tracing)
        if (config.Observability.EnableMetrics.GetValueOrDefault()
            || config.Observability.EnableTracing.GetValueOrDefault())
        {
            IMetricsCollector metrics = provider.GetRequiredService<IMetricsCollector>();
            current = new InstrumentedGhostData(current, metrics, G.GetLogger());
            G.LogInfo("Applied instrumentation decorator to data layer.");
        }

        return current;
    }

#endregion

#region Monitoring Configuration

    private void ConfigureMonitoringServices(IServiceCollection services)
    {
        services.AddSingleton<IMetricsCollector>(provider =>
        {
            GhostConfig config = provider.GetRequiredService<GhostConfig>();
            TimeSpan interval = TimeSpan.FromSeconds(config.Observability.MetricsIntervalSeconds.GetValueOrDefault(10));
            return new MetricsCollector(interval);
        });
    }

#endregion

#region Helper Methods

    /// <summary>
    ///     Creates a default logger for cases where G.Initialize() hasn't been called yet.
    ///     This provides backward compatibility for apps that don't use the new entry point pattern.
    /// </summary>
    private IGhostLogger CreateDefaultLogger()
    {
        GhostLoggerConfiguration loggerConfig = new GhostLoggerConfiguration
        {
                LogsPath = Config?.Core?.LogsPath ?? "logs",
                OutputsPath = Path.Combine(Config?.Core?.LogsPath ?? "logs", "outputs"),
                LogLevel = Config?.Core?.LogLevel ?? LogLevel.Information
        };

        // Create cache first (logger needs it)
        MemoryCache cache = new MemoryCache(null); // Bootstrap without logger

        // Create logger
        DefaultGhostLogger logger = new DefaultGhostLogger(loggerConfig);
        logger.SetCache(cache);

        // Set cache in G static class
        G.SetCache(cache);

        // Now create proper cache with logger and replace it
        MemoryCache properCache = new MemoryCache(logger);
        G.SetCache(properCache);
        cache.DisposeAsync(); // Clean up bootstrap cache

        return logger;
    }

#endregion

    /// <summary>
    ///     Abstract method for derived applications to register their specific services.
    ///     This is called after all basic services (cache, bus, database) are configured.
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Override in derived classes to add application-specific services
    }

#endregion
}
