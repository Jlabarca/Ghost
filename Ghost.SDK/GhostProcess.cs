using Ghost.Core.Config;
using Ghost.Core.Configuration;
using Ghost.Core.Data;
using Ghost.Core.Data.Decorators;
using Ghost.Core.Data.Implementations;
using Ghost.Core.Logging;
using Ghost.Core.Monitoring;
using Ghost.Core.Pooling;
using Ghost.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace Ghost
{
  /// <summary>
  /// Central access point for Ghost subsystems and services
  /// </summary>
  public class GhostProcess : IAsyncDisposable
  {
    private static readonly GhostProcess _instance = new GhostProcess();
    private readonly AsyncLocal<GhostApp> _currentApp = new AsyncLocal<GhostApp>();
    private readonly ConcurrentDictionary<string, GhostApp> _registeredApps = new ConcurrentDictionary<string, GhostApp>();
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    // Core subsystems
    private GhostConfig _config;
    private ICache _cache;
    private IGhostBus _bus;
    private IGhostData _data;
    private IMetricsCollector _metricsCollector;
    private bool _isInitialized;
    private ServiceProvider _serviceProvider;
    private ServiceCollection _services;

    // Static accessor for the singleton instance
    public static GhostProcess Instance => _instance;

    // Current app in execution context
    public GhostApp CurrentApp => _currentApp.Value;

    // Subsystem accessors
    public GhostConfig Config => _config;
    public ICache Cache => _cache;
    public IGhostBus Bus => _bus;
    public IGhostData Data => _data;
    public IMetricsCollector Metrics => _metricsCollector;

    // Private constructor to enforce singleton
    internal GhostProcess() { }

    /// <summary>
    /// Initialize the Ghost process with an application
    /// </summary>
    /// <param name="app">The application to initialize with</param>
    public void Initialize(GhostApp app)
    {
      if (app == null) throw new ArgumentNullException(nameof(app));

      // Set current app
      _currentApp.Value = app;
      app.GhostProcess = this;
      _services = app.Services;

      // Register app with unique ID
      string appId = app.Config?.App.Id ?? Guid.NewGuid().ToString();
      _registeredApps.TryAdd(appId, app);

      // Initialize core systems if needed
      if (!_isInitialized)
      {
        Initialize(app.Config);
      }
    }

    /// <summary>
    /// Initialize with explicit configuration
    /// </summary>
    /// <param name="config">Configuration to use</param>
    private void Initialize(GhostConfig config)
    {
      _lock.Wait();
      try
      {
        if (_isInitialized) return;

        // Store configuration
        _config = config ?? throw new ArgumentNullException(nameof(config));


        // Initialize all services using dependency injection
        ConfigureServices(config);
        _serviceProvider = _services.BuildServiceProvider();

        // Set up core services
        SetupCoreServices();

        _isInitialized = true;
        L.LogInfo("GhostProcess initialized successfully");
      }
      finally
      {
        _lock.Release();
      }
    }

    /// <summary>
    /// Configure all services for dependency injection
    /// </summary>
    /// <param name="config">Application configuration</param>
    private void ConfigureServices(GhostConfig config)
    {
      // Register configuration
      _services.AddSingleton(config);

      //StateManager


      _services.AddSingleton<IGhostBus>(sp => new GhostBus(sp.GetRequiredService<ICache>()));

      // Register logging
      _services.AddLogging(builder =>
      {
        builder.SetMinimumLevel(LogLevel.Debug);
      });

      // Setup cache paths
      string dataPath = config.Core.DataPath ?? Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
          "Ghost", "data");
      Directory.CreateDirectory(dataPath);

      string cachePath = Path.Combine(dataPath, "cache");
      Directory.CreateDirectory(cachePath);

      // Configure logger
      var loggerConfig = new GhostLoggerConfiguration
      {
          LogsPath = config.Core.LogsPath ?? "logs",
          OutputsPath = Path.Combine(config.Core.LogsPath ?? "logs", "outputs"),
          LogLevel = LogLevel.Debug,
      };

      _services.AddSingleton<IGhostLogger>(sp =>
      {
        var logger = new DefaultGhostLogger(loggerConfig);
        L.Initialize(logger);
        G.Initialize(logger);
        return logger;
      });
      _services.AddSingleton<ICache>(sp => new MemoryCache(sp.GetRequiredService<IGhostLogger>()));
      _services.AddSingleton<IGhostBus>(sp => new GhostBus(sp.GetRequiredService<ICache>()));

      // Register options
      var postgresConfig = GetPostgresConfig();
      _services.AddSingleton(Options.Create(postgresConfig));

      var cachingConfig = new CachingConfiguration
      {
          UseL1Cache = true,
          DefaultL1Expiration = TimeSpan.FromMinutes(5),
          DefaultL1SlidingExpiration = TimeSpan.FromMinutes(1),
          MaxL1CacheItems = 10000
      };
      _services.AddSingleton(Options.Create(cachingConfig));

      var resilienceConfig = new ResilienceConfiguration
      {
          EnableRetry = true,
          RetryCount = config.Core.MaxRetries,
          RetryBaseDelayMs = (int)config.Core.RetryDelay.TotalMilliseconds,
          EnableCircuitBreaker = true,
          CircuitBreakerThreshold = 5,
          CircuitBreakerDurationMs = 30000
      };
      _services.AddSingleton(Options.Create(resilienceConfig));

      // Register metrics
      _services.AddSingleton<IMetricsCollector>(sp =>
          new MetricsCollector(config.Core.MetricsInterval));

      // Register DB connection pooling
      _services.AddSingleton<ConnectionPoolManager>();

      // Register database client
      _services.AddSingleton<IDatabaseClient>(sp =>
          new PostgreSqlClient(
              postgresConfig.ConnectionString,
              sp.GetRequiredService<ILogger<PostgreSqlClient>>()));

      // Register schema manager
      _services.AddSingleton<ISchemaManager, PostgresSchemaManager>();

      // Register data layer with decorator pattern
// 1. Register the core implementation
      _services.AddSingleton<CoreGhostData>();

// 2. Register decorators with explicit factory functions
      _services.AddSingleton<IGhostData>(sp =>
      {
        // Start with the innermost implementation
        var core = sp.GetRequiredService<CoreGhostData>();

        // Add resilient layer
        var resilient = new ResilientGhostData(
            core,
            sp.GetRequiredService<ILogger<ResilientGhostData>>(),
            sp.GetRequiredService<IOptions<ResilienceConfiguration>>()
        );

        // Add caching layer
        var cached = new CachedGhostData(
            resilient,
            sp.GetRequiredService<ICache>(),
            sp.GetRequiredService<IOptions<CachingConfiguration>>(),
            sp.GetRequiredService<ILogger<CachedGhostData>>()
          );

        // Add instrumentation layer (outermost)
        var instrumented = new InstrumentedGhostData(
            cached,
            sp.GetRequiredService<IMetricsCollector>(),
            sp.GetRequiredService<ILogger<InstrumentedGhostData>>()
        );

        // Return the fully decorated chain
        return instrumented;
      });
    }

    /// <summary>
    /// Set up core services after dependency injection
    /// </summary>
    private void SetupCoreServices()
    {
      // Retrieve all core services from the DI container
      try
      {
        _cache = _serviceProvider.GetRequiredService<ICache>();
        _bus = _serviceProvider.GetRequiredService<IGhostBus>();
        _data = _serviceProvider.GetRequiredService<IGhostData>();
        _metricsCollector = _serviceProvider.GetRequiredService<IMetricsCollector>();

        // Initialize logger for global access
        var logger = _serviceProvider.GetRequiredService<IGhostLogger>();
        L.Initialize(logger);
        G.Initialize(logger);
      }
      catch (Exception e)
      {
        L.LogError(e, "Failed to set up core services");
        throw;
      }

      // Initialize the data service
      //_data.InitializeAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Get the PostgreSQL configuration
    /// </summary>
    private PostgresConfiguration GetPostgresConfig()
    {
      var postgresConfig = new PostgresConfiguration
      {
          ConnectionString = "Host=localhost;Database=ghost;Username=ghost;Password=ghost",
          MaxPoolSize = 100,
          MinPoolSize = 5,
          PrewarmConnections = false,
          ConnectionLifetime = TimeSpan.FromMinutes(30),
          ConnectionIdleLifetime = TimeSpan.FromMinutes(5),
          CommandTimeout = 30,
          EnableParameterLogging = false,
          Schema = "public",
          EnablePooling = true
      };

      // Use module config if available
      if (Config?.HasModule("postgres") == true)
      {
          var pgConfig = Config.GetModuleConfig<PostgresConfig>("postgres");
          if (pgConfig != null)
          {
              postgresConfig.ConnectionString = pgConfig.ConnectionString;
              postgresConfig.MaxPoolSize = pgConfig.MaxPoolSize;
              postgresConfig.CommandTimeout = (int)pgConfig.CommandTimeout.TotalSeconds;
          }
      }

      return postgresConfig;
    }

    #region Metrics Methods

    /// <summary>
    /// Track a metric value
    /// </summary>
    public async Task TrackMetricAsync(string name, double value, Dictionary<string, string> tags = null)
    {
      EnsureInitialized();
      await _metricsCollector.TrackMetricAsync(new MetricValue(
          name, value, tags ?? new Dictionary<string, string>(), DateTime.UtcNow
      ));
    }

    /// <summary>
    /// Track a named event
    /// </summary>
    public async Task TrackEventAsync(string name, Dictionary<string, string> properties = null)
    {
      EnsureInitialized();
      await _bus.PublishAsync(
          $"ghost:events:{CurrentApp?.Config?.App?.Id ?? "app"}",
          new
          {
              Name = name,
              Properties = properties ?? new Dictionary<string, string>(),
              Timestamp = DateTime.UtcNow
          }
      );
    }

    #endregion

    #region Data Methods

    /// <summary>
    /// Execute a SQL command
    /// </summary>
    public async Task<int> ExecuteAsync(string sql, object param = null)
    {
      EnsureInitialized();
      return await _data.ExecuteAsync(sql, param);
    }

    /// <summary>
    /// Query for data
    /// </summary>
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
    {
      EnsureInitialized();
      return await _data.QueryAsync<T>(sql, param);
    }

    /// <summary>
    /// Get a configuration setting
    /// </summary>
    public string GetSetting(string name, string defaultValue = null)
    {
      EnsureInitialized();
      return _config.Core.Settings.TryGetValue(name, out var value) ? value : defaultValue;
    }

    #endregion

    #region Bus Methods

    /// <summary>
    /// Publish a message
    /// </summary>
    public async Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
    {
      EnsureInitialized();
      await _bus.PublishAsync(channel, message, expiry);
    }

    /// <summary>
    /// Subscribe to a channel
    /// </summary>
    public IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, CancellationToken cancellationToken = default)
    {
      EnsureInitialized();
      return _bus.SubscribeAsync<T>(channelPattern, cancellationToken);
    }

    #endregion

    /// <summary>
    /// Ensure the process is initialized
    /// </summary>
    private void EnsureInitialized()
    {
      if (!_isInitialized)
        throw new InvalidOperationException("GhostProcess has not been initialized. Call Ghost.Init() first.");
    }

    /// <summary>
    /// Shutdown and cleanup
    /// </summary>
    public async Task ShutdownAsync()
    {
      await _lock.WaitAsync();
      try
      {
        if (!_isInitialized) return;

        // Dispose all services through the service provider
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
          await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
          disposable.Dispose();
        }

        _isInitialized = false;
        L.LogInfo("GhostProcess shut down successfully");
      }
      finally
      {
        _lock.Release();
      }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public async ValueTask DisposeAsync()
    {
      await ShutdownAsync();
      _lock.Dispose();
    }
  }
}