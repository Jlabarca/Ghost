using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Logging;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
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
    private MetricsCollector _metricsCollector;
    private bool _isInitialized;
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
    public MetricsCollector Metrics => _metricsCollector;

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
        _services.AddSingleton(_config);

        // Initialize data paths
        string dataPath = config.Core.DataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ghost", "data");
        Directory.CreateDirectory(dataPath);

        // Cache initialization
        string cachePath = Path.Combine(dataPath, "cache");
        Directory.CreateDirectory(cachePath);
        _cache = new LocalCache(cachePath);

        // Logging initialization
        var loggerConfig = new GhostLoggerConfiguration
        {
            LogsPath = Config.Core.LogsPath ?? "logs",
            OutputsPath = Path.Combine(Config.Core.LogsPath ?? "logs", "outputs"),
            LogLevel = Microsoft.Extensions.Logging.LogLevel.Information
        };
        var logger = new DefaultGhostLogger(_cache, loggerConfig);
        L.Initialize(logger);

        // Bus initialization
        _bus = new GhostBus(_cache);
        _services.AddSingleton<IGhostBus>(_bus);

        // Database initialization
        _services.AddSingleton<IGhostLogger>(logger);
        G.Initialize(logger);

        // Add database services
        IDatabaseClient db = GetDatabaseClient();
        _services.AddSingleton(db);

        var kvStore = new SQLiteKeyValueStore(db);
        var schema = db.DatabaseType == DatabaseType.PostgreSQL
            ? new PostgresSchemaManager(db)
            : new SQLiteSchemaManager(db) as ISchemaManager;

        _data = new GhostData(db, kvStore, _cache, schema);
        _data.InitializeAsync().GetAwaiter().GetResult();
        _services.AddSingleton(_data);

        // Metrics initialization
        _metricsCollector = new MetricsCollector(config.Core.MetricsInterval);

        _isInitialized = true;
        L.LogInfo("GhostProcess initialized successfully");
      }
      finally
      {
        _lock.Release();
      }
    }

    /// <summary>
    /// Get the appropriate database client based on configuration
    /// </summary>
    private IDatabaseClient GetDatabaseClient()
    {
      // Use PostgreSQL if configured
      if (Config.HasModule("postgres"))
      {
        var pgConfig = Config.GetModuleConfig<PostgresConfig>("postgres");
        return new SQLiteDatabase(pgConfig.ConnectionString); // This would be PostgresDatabase in a full implementation
      }

      // Default to SQLite
      var dbPath = Path.Combine(
          Config.GetModuleConfig<LocalCacheConfig>("cache")?.Path ?? "data",
          "ghost.db");
      return new SQLiteDatabase(dbPath);
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

        // Dispose subsystems
        if (_data != null)
        {
          await _data.DisposeAsync();
          _data = null;
        }

        if (_bus != null)
        {
          await _bus.DisposeAsync();
          _bus = null;
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
