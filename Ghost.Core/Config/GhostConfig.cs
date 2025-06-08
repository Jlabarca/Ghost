using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using static Microsoft.Extensions.Logging.LogLevel;

namespace Ghost.Config;

public class GhostConfig
{
    public AppInfo? App { get; set; }
    public CoreConfig? Core { get; set; }
    public RedisDataConfig? Redis { get; set; }
    public PostgresDataConfig? Postgres { get; set; }
    public CachingDataConfig? Caching { get; set; }
    public ResilienceDataConfig? Resilience { get; set; }
    public SecurityDataConfig? Security { get; set; }
    public ObservabilityDataConfig? Observability { get; set; }
    public Dictionary<string, ModuleConfigBase>? Modules { get; set; }

    /// <summary>
    ///     Load base config with optional override
    /// </summary>
    public static async Task<GhostConfig> LoadAsync(string basePath = ".ghost.yaml", string? overridePath = null)
    {
        GhostConfig? baseConfig = await LoadSingleAsync(basePath);

        if (string.IsNullOrEmpty(overridePath) || !File.Exists(overridePath))
        {
            return ApplyDefaults(baseConfig);
        }

        GhostConfig? overrideConfig = await LoadSingleAsync(overridePath);
        return ApplyDefaults(baseConfig.MergeWith(overrideConfig));
    }

    private static async Task<GhostConfig> LoadSingleAsync(string path)
    {
        if (!File.Exists(path))
        {
            G.LogWarn($"Configuration file not found at {Path.GetFullPath(path)}");
            return new GhostConfig();
        }

        string? yaml = await File.ReadAllTextAsync(path);
        IDeserializer? deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

        return deserializer.Deserialize<GhostConfig>(yaml) ?? new GhostConfig();
    }

    /// <summary>
    ///     Merge this config with another, preferring non-null values from other
    /// </summary>
    public GhostConfig MergeWith(GhostConfig other)
    {
        return new GhostConfig
        {
                App = other.App?.MergeWith(App) ?? App,
                Core = other.Core?.MergeWith(Core) ?? Core,
                Redis = other.Redis?.MergeWith(Redis) ?? Redis,
                Postgres = other.Postgres?.MergeWith(Postgres) ?? Postgres,
                Caching = other.Caching?.MergeWith(Caching) ?? Caching,
                Resilience = other.Resilience?.MergeWith(Resilience) ?? Resilience,
                Security = other.Security?.MergeWith(Security) ?? Security,
                Observability = other.Observability?.MergeWith(Observability) ?? Observability,
                Modules = MergeDictionaries(Modules, other.Modules)
        };
    }

    private static GhostConfig ApplyDefaults(GhostConfig config)
    {
        config.App ??= AppInfo.CreateDefault();
        config.Core ??= CoreConfig.CreateDefault();
        config.Redis ??= RedisDataConfig.CreateDefault();
        config.Postgres ??= PostgresDataConfig.CreateDefault();
        config.Caching ??= CachingDataConfig.CreateDefault();
        config.Resilience ??= ResilienceDataConfig.CreateDefault();
        config.Security ??= SecurityDataConfig.CreateDefault();
        config.Observability ??= ObservabilityDataConfig.CreateDefault();
        config.Modules ??= new Dictionary<string, ModuleConfigBase>();

        return config;
    }

    private static Dictionary<string, ModuleConfigBase>? MergeDictionaries(
            Dictionary<string, ModuleConfigBase>? baseDict,
            Dictionary<string, ModuleConfigBase>? overrideDict)
    {
        if (overrideDict == null)
        {
            return baseDict;
        }
        if (baseDict == null)
        {
            return overrideDict;
        }

        var result = new Dictionary<string, ModuleConfigBase>(baseDict);
        foreach (var kvp in overrideDict)
        {
            result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    // Helper methods with null-safe operations
    public bool HasModule(string name)
    {
        return Modules?.ContainsKey(name) == true && Modules[name].Enabled;
    }

    public T? GetModuleConfig<T>(string name) where T : ModuleConfigBase
    {
        return (Modules?.TryGetValue(name, out ModuleConfigBase? config) == true && config.Enabled && config is T typedConfig)
                ? typedConfig : null;
    }

    public string GetLogsPath()
    {
        return Path.Combine(App?.Id ?? "ghost-app", Core?.LogsPath ?? "logs");
    }
    public string GetDataPath()
    {
        return Path.Combine(App?.Id ?? "ghost-app", Core?.DataPath ?? "data");
    }
    public string GetAppsPath()
    {
        return Path.Combine(App?.Id ?? "ghost-app", Core?.AppsPath ?? "ghosts");
    }

    public string? ToYaml()
    {
        ISerializer? serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
        return serializer.Serialize(this);
    }
}

// Configuration classes with nullable properties
public class AppInfo
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Version { get; set; }

    public AppInfo MergeWith(AppInfo? other)
    {
        return other == null ? this : new AppInfo
        {
                Id = other.Id ?? Id,
                Name = other.Name ?? Name,
                Description = other.Description ?? Description,
                Version = other.Version ?? Version
        };
    }

    public static AppInfo CreateDefault()
    {
        return new AppInfo
        {
                Id = "ghost-app",
                Name = "Ghost Application",
                Description = "A Ghost Application",
                Version = "1.0.0"
        };
    }
}
public class CoreConfig
{
    public string? Mode { get; set; }
    public string? LogsPath { get; set; }
    public LogLevel? LogLevel { get; set; }
    public string? DataPath { get; set; }
    public string? AppsPath { get; set; }
    public int? ListenPort { get; set; }
    public TimeSpan? HealthCheckInterval { get; set; }
    public TimeSpan? MetricsInterval { get; set; }
    public Dictionary<string, string>? Settings { get; set; }
    public List<string>? WatchPaths { get; set; }
    public List<string>? WatchIgnore { get; set; }
    public string? GhostFatherHost { get; set; }
    public int? GhostFatherPort { get; set; }
    public int? MaxRetries { get; set; }
    public TimeSpan? RetryDelay { get; set; }
    public bool? UseInMemoryDatabase { get; set; }

    public CoreConfig MergeWith(CoreConfig? other)
    {
        return other == null ? this : new CoreConfig
        {
                Mode = other.Mode ?? Mode,
                LogsPath = other.LogsPath ?? LogsPath,
                LogLevel = other.LogLevel ?? LogLevel,
                DataPath = other.DataPath ?? DataPath,
                AppsPath = other.AppsPath ?? AppsPath,
                ListenPort = other.ListenPort ?? ListenPort,
                HealthCheckInterval = other.HealthCheckInterval ?? HealthCheckInterval,
                MetricsInterval = other.MetricsInterval ?? MetricsInterval,
                Settings = other.Settings ?? Settings,
                WatchPaths = other.WatchPaths ?? WatchPaths,
                WatchIgnore = other.WatchIgnore ?? WatchIgnore,
                GhostFatherHost = other.GhostFatherHost ?? GhostFatherHost,
                GhostFatherPort = other.GhostFatherPort ?? GhostFatherPort,
                MaxRetries = other.MaxRetries ?? MaxRetries,
                RetryDelay = other.RetryDelay ?? RetryDelay,
                UseInMemoryDatabase = other.UseInMemoryDatabase ?? UseInMemoryDatabase
        };
    }

    public static CoreConfig CreateDefault()
    {
        return new CoreConfig
        {
                Mode = "development",
                LogsPath = "logs",
                LogLevel = Debug,
                DataPath = "data",
                AppsPath = "ghosts",
                ListenPort = 0,
                HealthCheckInterval = TimeSpan.FromSeconds(30),
                MetricsInterval = TimeSpan.FromSeconds(5),
                Settings = new Dictionary<string, string>
                {
                        ["autoGhostFather"] = "true",
                        ["autoMonitor"] = "true"
                },
                WatchPaths = new List<string>(),
                WatchIgnore = new List<string>
                {
                        "*.log",
                        "*.tmp"
                },
                GhostFatherHost = "localhost",
                GhostFatherPort = 5000,
                MaxRetries = 3,
                RetryDelay = TimeSpan.FromSeconds(1),
                UseInMemoryDatabase = false
        };
    }
}
public abstract class ModuleConfigBase
{
    public bool Enabled { get; set; } = true;
}
public class RedisDataConfig : ModuleConfigBase
{
    public string? ConnectionString { get; set; }
    public int? Database { get; set; }
    public bool? UseSsl { get; set; }
    public int? ConnectRetry { get; set; }
    public TimeSpan? ConnectTimeout { get; set; }
    public TimeSpan? SyncTimeout { get; set; }
    public TimeSpan? CommandTimeout { get; set; }
    public string? KeyPrefix { get; set; }

    public RedisDataConfig MergeWith(RedisDataConfig? other)
    {
        return other == null ? this : new RedisDataConfig
        {
                Enabled = other.Enabled,
                ConnectionString = other.ConnectionString ?? ConnectionString,
                Database = other.Database ?? Database,
                UseSsl = other.UseSsl ?? UseSsl,
                ConnectRetry = other.ConnectRetry ?? ConnectRetry,
                ConnectTimeout = other.ConnectTimeout ?? ConnectTimeout,
                SyncTimeout = other.SyncTimeout ?? SyncTimeout,
                CommandTimeout = other.CommandTimeout ?? CommandTimeout,
                KeyPrefix = other.KeyPrefix ?? KeyPrefix
        };
    }

    public static RedisDataConfig CreateDefault()
    {
        return new RedisDataConfig
        {
                ConnectionString = "localhost:6379",
                Database = 0,
                UseSsl = false,
                ConnectRetry = 3,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                SyncTimeout = TimeSpan.FromSeconds(5),
                CommandTimeout = TimeSpan.FromSeconds(10),
                KeyPrefix = ""
        };
    }
}
public class PostgresDataConfig : ModuleConfigBase
{
    public string? ConnectionString { get; set; }
    public int? MaxPoolSize { get; set; }
    public int? MinPoolSize { get; set; }
    public bool? PrewarmConnections { get; set; }
    public TimeSpan? ConnectionLifetime { get; set; }
    public TimeSpan? ConnectionIdleLifetime { get; set; }
    public int? CommandTimeout { get; set; }
    public bool? EnableParameterLogging { get; set; }
    public string? Schema { get; set; }
    public bool? EnablePooling { get; set; }

    public PostgresDataConfig MergeWith(PostgresDataConfig? other)
    {
        return other == null ? this : new PostgresDataConfig
        {
                Enabled = other.Enabled,
                ConnectionString = other.ConnectionString ?? ConnectionString,
                MaxPoolSize = other.MaxPoolSize ?? MaxPoolSize,
                MinPoolSize = other.MinPoolSize ?? MinPoolSize,
                PrewarmConnections = other.PrewarmConnections ?? PrewarmConnections,
                ConnectionLifetime = other.ConnectionLifetime ?? ConnectionLifetime,
                ConnectionIdleLifetime = other.ConnectionIdleLifetime ?? ConnectionIdleLifetime,
                CommandTimeout = other.CommandTimeout ?? CommandTimeout,
                EnableParameterLogging = other.EnableParameterLogging ?? EnableParameterLogging,
                Schema = other.Schema ?? Schema,
                EnablePooling = other.EnablePooling ?? EnablePooling
        };
    }

    public static PostgresDataConfig CreateDefault()
    {
        return new PostgresDataConfig
        {
                ConnectionString = "Host=localhost;Database=ghost;",
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
    }
}
public class CachingDataConfig : ModuleConfigBase
{
    public bool? UseL1Cache { get; set; }
    public bool? UseL2Cache { get; set; }
    public TimeSpan? DefaultExpiration { get; set; }
    public TimeSpan? DefaultL1SlidingExpiration { get; set; }
    public TimeSpan? DefaultL1Expiration { get; set; }
    public TimeSpan? DefaultL2Expiration { get; set; }
    public int? MaxL1CacheItems { get; set; }
    public long? MaxL1CacheSizeBytes { get; set; }
    public string? CachePath { get; set; }
    public bool? CompressItems { get; set; }
    public int? CompressionThreshold { get; set; }

    public CachingDataConfig MergeWith(CachingDataConfig? other)
    {
        return other == null ? this : new CachingDataConfig
        {
                Enabled = other.Enabled,
                UseL1Cache = other.UseL1Cache ?? UseL1Cache,
                UseL2Cache = other.UseL2Cache ?? UseL2Cache,
                DefaultExpiration = other.DefaultExpiration ?? DefaultExpiration,
                DefaultL1SlidingExpiration = other.DefaultL1SlidingExpiration ?? DefaultL1SlidingExpiration,
                DefaultL1Expiration = other.DefaultL1Expiration ?? DefaultL1Expiration,
                DefaultL2Expiration = other.DefaultL2Expiration ?? DefaultL2Expiration,
                MaxL1CacheItems = other.MaxL1CacheItems ?? MaxL1CacheItems,
                MaxL1CacheSizeBytes = other.MaxL1CacheSizeBytes ?? MaxL1CacheSizeBytes,
                CachePath = other.CachePath ?? CachePath,
                CompressItems = other.CompressItems ?? CompressItems,
                CompressionThreshold = other.CompressionThreshold ?? CompressionThreshold
        };
    }

    public static CachingDataConfig CreateDefault()
    {
        return new CachingDataConfig
        {
                UseL1Cache = true,
                UseL2Cache = true,
                DefaultExpiration = TimeSpan.FromMinutes(5),
                DefaultL1SlidingExpiration = TimeSpan.FromMinutes(1),
                DefaultL1Expiration = TimeSpan.FromMinutes(5),
                DefaultL2Expiration = TimeSpan.FromMinutes(30),
                MaxL1CacheItems = 10000,
                MaxL1CacheSizeBytes = 100 * 1024 * 1024,
                CachePath = "cache",
                CompressItems = false,
                CompressionThreshold = 1024
        };
    }
}
public class ResilienceDataConfig
{
    public bool? EnableRetry { get; set; }
    public int? RetryCount { get; set; }
    public int? RetryBaseDelayMs { get; set; }
    public bool? EnableCircuitBreaker { get; set; }
    public int? CircuitBreakerThreshold { get; set; }
    public int? CircuitBreakerDurationMs { get; set; }
    public int? TimeoutMs { get; set; }
    public bool? EnableBulkhead { get; set; }
    public int? MaxConcurrency { get; set; }
    public int? MaxQueueSize { get; set; }

    public ResilienceDataConfig MergeWith(ResilienceDataConfig? other)
    {
        return other == null ? this : new ResilienceDataConfig
        {
                EnableRetry = other.EnableRetry ?? EnableRetry,
                RetryCount = other.RetryCount ?? RetryCount,
                RetryBaseDelayMs = other.RetryBaseDelayMs ?? RetryBaseDelayMs,
                EnableCircuitBreaker = other.EnableCircuitBreaker ?? EnableCircuitBreaker,
                CircuitBreakerThreshold = other.CircuitBreakerThreshold ?? CircuitBreakerThreshold,
                CircuitBreakerDurationMs = other.CircuitBreakerDurationMs ?? CircuitBreakerDurationMs,
                TimeoutMs = other.TimeoutMs ?? TimeoutMs,
                EnableBulkhead = other.EnableBulkhead ?? EnableBulkhead,
                MaxConcurrency = other.MaxConcurrency ?? MaxConcurrency,
                MaxQueueSize = other.MaxQueueSize ?? MaxQueueSize
        };
    }

    public static ResilienceDataConfig CreateDefault()
    {
        return new ResilienceDataConfig
        {
                EnableRetry = true,
                RetryCount = 3,
                RetryBaseDelayMs = 100,
                EnableCircuitBreaker = true,
                CircuitBreakerThreshold = 5,
                CircuitBreakerDurationMs = 30000,
                TimeoutMs = 30000,
                EnableBulkhead = false,
                MaxConcurrency = 100,
                MaxQueueSize = 50
        };
    }
}
public class SecurityDataConfig
{
    public bool? EnableEncryption { get; set; }
    public string? EncryptionKey { get; set; }
    public string? EncryptionIV { get; set; }
    public string? EncryptionAlgorithm { get; set; }
    public bool? EnableAudit { get; set; }
    public bool? EnableAccessControl { get; set; }
    public string? AuditLogPath { get; set; }
    public int? AuditLogRetentionDays { get; set; }

    public SecurityDataConfig MergeWith(SecurityDataConfig? other)
    {
        return other == null ? this : new SecurityDataConfig
        {
                EnableEncryption = other.EnableEncryption ?? EnableEncryption,
                EncryptionKey = other.EncryptionKey ?? EncryptionKey,
                EncryptionIV = other.EncryptionIV ?? EncryptionIV,
                EncryptionAlgorithm = other.EncryptionAlgorithm ?? EncryptionAlgorithm,
                EnableAudit = other.EnableAudit ?? EnableAudit,
                EnableAccessControl = other.EnableAccessControl ?? EnableAccessControl,
                AuditLogPath = other.AuditLogPath ?? AuditLogPath,
                AuditLogRetentionDays = other.AuditLogRetentionDays ?? AuditLogRetentionDays
        };
    }

    public static SecurityDataConfig CreateDefault()
    {
        return new SecurityDataConfig
        {
                EnableEncryption = false,
                EncryptionKey = "",
                EncryptionIV = "",
                EncryptionAlgorithm = "AES",
                EnableAudit = false,
                EnableAccessControl = false,
                AuditLogPath = "logs/audit",
                AuditLogRetentionDays = 90
        };
    }
}
public class ObservabilityDataConfig : ModuleConfigBase
{
    public string? LogLevel { get; set; }
    public bool? EnableConsoleLogging { get; set; }
    public bool? EnableFileLogging { get; set; }
    public bool? EnableMetrics { get; set; }
    public bool? EnableTracing { get; set; }
    public bool? EnableHealthChecks { get; set; }
    public string? LogsPath { get; set; }
    public int? LogRetentionDays { get; set; }
    public int? MetricsIntervalSeconds { get; set; }
    public int? HealthCheckIntervalSeconds { get; set; }
    public bool? EnableStructuredLogging { get; set; }
    public double? TracingSamplingRate { get; set; }

    public ObservabilityDataConfig MergeWith(ObservabilityDataConfig? other)
    {
        return other == null ? this : new ObservabilityDataConfig
        {
                Enabled = other.Enabled,
                LogLevel = other.LogLevel ?? LogLevel,
                EnableConsoleLogging = other.EnableConsoleLogging ?? EnableConsoleLogging,
                EnableFileLogging = other.EnableFileLogging ?? EnableFileLogging,
                EnableMetrics = other.EnableMetrics ?? EnableMetrics,
                EnableTracing = other.EnableTracing ?? EnableTracing,
                EnableHealthChecks = other.EnableHealthChecks ?? EnableHealthChecks,
                LogsPath = other.LogsPath ?? LogsPath,
                LogRetentionDays = other.LogRetentionDays ?? LogRetentionDays,
                MetricsIntervalSeconds = other.MetricsIntervalSeconds ?? MetricsIntervalSeconds,
                HealthCheckIntervalSeconds = other.HealthCheckIntervalSeconds ?? HealthCheckIntervalSeconds,
                EnableStructuredLogging = other.EnableStructuredLogging ?? EnableStructuredLogging,
                TracingSamplingRate = other.TracingSamplingRate ?? TracingSamplingRate
        };
    }

    public static ObservabilityDataConfig CreateDefault()
    {
        return new ObservabilityDataConfig
        {
                LogLevel = "Information",
                EnableConsoleLogging = true,
                EnableFileLogging = true,
                EnableMetrics = true,
                EnableTracing = false,
                EnableHealthChecks = true,
                LogsPath = "logs",
                LogRetentionDays = 7,
                MetricsIntervalSeconds = 15,
                HealthCheckIntervalSeconds = 30,
                EnableStructuredLogging = true,
                TracingSamplingRate = 0.1
        };
    }
}
