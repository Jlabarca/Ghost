using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Logging;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Ghost.SDK;

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
    private GhostFatherConnection _connection;
    
    private bool _isInitialized;

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
    
    // Initialize with app
    public void Initialize(GhostApp app)
    {
        if (app == null) throw new ArgumentNullException(nameof(app));
        
        _currentApp.Value = app;
        string appId = app.Config?.App?.Id ?? Guid.NewGuid().ToString();
        _registeredApps.TryAdd(appId, app);
        
        if (!_isInitialized)
        {
            Initialize(app.Config);
        }
        
        // Set up GhostFather if configured
        if (app.AutoGhostFather)
        {
            InitializeGhostFather(app);
        }
    }
    
    // Initialize with explicit config
    public void Initialize(GhostConfig config)
    {
        _lock.Wait();
        try
        {
            if (_isInitialized) return;
            
            // Initialize all subsystems in one place
            _config = config;
            
            // Initialize cache and paths
            string dataPath = config.Core.DataPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ghost", "data");
            Directory.CreateDirectory(dataPath);
            
            // Cache initialization
            string cachePath = Path.Combine(dataPath, "cache");
            Directory.CreateDirectory(cachePath);
            _cache = new LocalCache(cachePath);

            var loggerConfig = new GhostLoggerConfiguration
            {
                    LogsPath = Config.Core.LogsPath ?? "logs",
                    OutputsPath = Path.Combine(Config.Core.LogsPath ?? "logs", "outputs"),
                    LogLevel = Microsoft.Extensions.Logging.LogLevel.Information
            };

            var logger = new DefaultGhostLogger(_cache, loggerConfig);
            G.Initialize(logger);
            
            // Bus initialization
            _bus = new GhostBus(_cache);
            
            // Database initialization
            var services = new ServiceCollection();
            services.AddSingleton<IGhostLogger>(logger);

            // Add database services...
            IDatabaseClient db = GetDatabaseClient();
            services.AddSingleton(db);
            var kvStore = new SQLiteKeyValueStore(db);
            
            var schema = db.DatabaseType == DatabaseType.PostgreSQL
                ? new PostgresSchemaManager(db)
                : new SQLiteSchemaManager(db) as ISchemaManager;
                
            _data = new GhostData(db, kvStore, _cache, schema);
            _data.InitializeAsync().GetAwaiter().GetResult();
            
            // Metrics initialization
            _metricsCollector = new MetricsCollector(config.Core.MetricsInterval);
            
            _isInitialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }
    private IDatabaseClient GetDatabaseClient()
    {
        if (Config.HasModule("postgres"))
        {
            var pgConfig = Config.GetModuleConfig<PostgresConfig>("postgres");
            return new SQLiteDatabase(pgConfig.ConnectionString);
        }

        var dbPath = Path.Combine(
                Config.GetModuleConfig<LocalCacheConfig>("cache")?.Path ?? "data",
                "ghost.db"
        );
        return new SQLiteDatabase(dbPath);
    }

    // Methods for each feature area (metrics, data, etc.)
    // Now implemented directly in the GhostProcess class

    // Metrics methods
    public async Task TrackMetricAsync(string name, double value, Dictionary<string, string> tags = null)
    {
        EnsureInitialized();
        await _metricsCollector.TrackMetricAsync(new MetricValue(
            name, value, tags ?? new Dictionary<string, string>(), DateTime.UtcNow
        ));
    }
    
    public async Task TrackEventAsync(string name, Dictionary<string, string> properties = null)
    {
        EnsureInitialized();
        await _bus.PublishAsync($"ghost:events:{CurrentApp?.Config?.App?.Id ?? "app"}", new
        {
            Name = name,
            Properties = properties ?? new Dictionary<string, string>(),
            Timestamp = DateTime.UtcNow
        });
    }
    
    // Data methods
    public async Task<int> ExecuteAsync(string sql, object param = null)
    {
        EnsureInitialized();
        return await _data.ExecuteAsync(sql, param);
    }
    
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
    {
        EnsureInitialized();
        return await _data.QueryAsync<T>(sql, param);
    }
    
    public string GetSetting(string name, string defaultValue = null)
    {
        EnsureInitialized();
        return _config.Core.Settings.TryGetValue(name, out var value) ? value : defaultValue;
    }
    
    // Bus methods
    public async Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
    {
        EnsureInitialized();
        await _bus.PublishAsync(channel, message, expiry);
    }
    
    public IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return _bus.SubscribeAsync<T>(channelPattern, cancellationToken);
    }
    
    // Helper methods
    private void EnsureInitialized()
    {
        if (!_isInitialized)
            throw new InvalidOperationException("GhostProcess has not been initialized. Call Ghost.Init() first.");
    }
    
    private void InitializeGhostFather(GhostApp app)
    {
        // GhostFather initialization logic...
    }
    
    // Shutdown and cleanup
    public async Task ShutdownAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!_isInitialized) return;
            
            // Dispose connection
            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
            
            // Dispose all subsystems
            await _data.DisposeAsync();
            _data = null;
            await _bus.DisposeAsync();
            _bus = null;
            
            _isInitialized = false;
        }
        finally
        {
            _lock.Release();
        }
    }
    
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _lock.Dispose();
    }
}