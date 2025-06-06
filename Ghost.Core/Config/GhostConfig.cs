using Microsoft.Extensions.Logging; // For LogLevel in CoreConfig and ObservabilityDataConfig
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Collections.Generic; // For Dictionary
using System.IO; // For File operations
using System.Threading.Tasks; // For Task
using System; // For TimeSpan

namespace Ghost.Config
{
  /// <summary>
  /// Main configuration class for Ghost applications, incorporating detailed data layer settings.
  /// </summary>
  public class GhostConfig
  {
    // Existing top-level properties from your Ghost.Config
    public AppInfo App { get; set; } = new();
    public CoreConfig Core { get; set; } = new();

    // --- Fused Data Layer Configurations ---
    // These sections now hold the detailed settings previously in Ghost.Configuration.*
    public RedisDataConfig Redis { get; set; } = new();
    public PostgresDataConfig Postgres { get; set; } = new();
    public CachingDataConfig Caching { get; set; } = new();
    public ResilienceDataConfig Resilience { get; set; } = new();
    public SecurityDataConfig Security { get; set; } = new();
    public ObservabilityDataConfig Observability { get; set; } = new();

    /// <summary>
    /// For other, non-data-layer specific modules or truly optional components.
    /// The concrete types stored here would derive from ModuleConfigBase.
    /// </summary>
    public Dictionary<string, ModuleConfigBase> Modules { get; set; } = new();

    public bool HasModule(string name) => Modules.ContainsKey(name) && Modules[name].Enabled;

    public T? GetModuleConfig<T>(string name) where T : ModuleConfigBase =>
        (Modules.TryGetValue(name, out var config) && config.Enabled && config is T typedConfig) ? typedConfig : null;

    // --- Existing Helper Methods ---
    public static async Task<GhostConfig?> LoadAsync(string path = ".ghost.yaml")
    {
      if (!File.Exists(path))
      {
        // Consider logging a warning or returning a default config instead of null
        // For example: Console.WriteLine($"Warning: Configuration file not found at {Path.GetFullPath(path)}. Using default configuration.");
        // return new GhostConfig(); // Or throw
        G.LogWarn($"Configuration file not found at {Path.GetFullPath(path)}. Using default configuration.");
        return new GhostConfig();
      }
      var yaml = await File.ReadAllTextAsync(path);
      var deserializer = new DeserializerBuilder()
          .WithNamingConvention(CamelCaseNamingConvention.Instance)
          .Build();
      return deserializer.Deserialize<GhostConfig>(yaml);
    }

    public string GetLogsPath() => Path.Combine(App?.Id ?? "unknown-app", Core?.LogsPath ?? "logs");
    public string GetDataPath() => Path.Combine(App?.Id ?? "unknown-app", Core?.DataPath ?? "data");
    public string GetAppsPath() => Path.Combine(App?.Id ?? "unknown-app", Core?.AppsPath ?? "ghosts");

    public string? ToYaml()
    {
      var serializer = new SerializerBuilder()
          .WithNamingConvention(CamelCaseNamingConvention.Instance)
          .Build();
      return serializer.Serialize(this);
    }
  }

  // --- AppInfo and CoreConfig (Your existing definitions from Ghost.Config) ---
  public class AppInfo
  {
    public string Id { get; set; } = "ghost-app";
    public string Name { get; set; } = "Ghost Application";
    public string Description { get; set; } = "A Ghost Application";
    public string Version { get; set; } = "1.0.0";
  }

  public class CoreConfig
  {
    public string Mode { get; set; } = "development"; // e.g., development, production
    public string LogsPath { get; set; } = "logs";
    public LogLevel LogLevel { get; set; } = LogLevel.Debug;
    public string DataPath { get; set; } = "data";
    public string AppsPath { get; set; } = "ghosts";
    public int ListenPort { get; set; } = 0; // 0 = auto-assign
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);
    public Dictionary<string, string> Settings { get; set; } = new()
    {
        ["autoGhostFather"] = "true",
        ["autoMonitor"] = "true"
    };
    public List<string> WatchPaths { get; set; } = new();
    public List<string> WatchIgnore { get; set; } = new()
    {
        "*.log",
        "*.tmp"
    };
    public string GhostFatherHost { get; set; } = "localhost";
    public int GhostFatherPort { get; set; } = 5000;
    public int MaxRetries { get; set; } = 3; // Can be used for resilience defaults
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1); // Can be used for resilience defaults
    public bool UseInMemoryDatabase { get; set; } = false; // For testing or lightweight scenarios

  }

  /// <summary>
  /// Base class for configurations in the Modules dictionary.
  /// </summary>
  public abstract class ModuleConfigBase
  {
    public bool Enabled { get; set; } = true;
  }

  // --- DETAILED DATA LAYER CONFIGURATION CLASSES (Fused into Ghost.Config) ---
  // These mirror the structures previously named Ghost.Configuration.*Configuration

  public class RedisDataConfig : ModuleConfigBase
  {
    public string ConnectionString { get; set; } = "localhost:6379";
    public int Database { get; set; } = 0;
    public bool UseSsl { get; set; } = false;
    public int ConnectRetry { get; set; } = 3;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public string KeyPrefix { get; set; } = "";
  }

  public class PostgresDataConfig : ModuleConfigBase
  {
    public string ConnectionString { get; set; } = "Host=localhost;Database=ghost;";
    public int MaxPoolSize { get; set; } = 100;
    public int MinPoolSize { get; set; } = 5;
    public bool PrewarmConnections { get; set; } = false;
    public TimeSpan ConnectionLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public TimeSpan ConnectionIdleLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int CommandTimeout { get; set; } = 30; // Seconds
    public bool EnableParameterLogging { get; set; } = false;
    public string Schema { get; set; } = "public";
    public bool EnablePooling { get; set; } = true;
  }

  public class CachingDataConfig : ModuleConfigBase
  {
    public bool UseL1Cache { get; set; } = true; // L1 in-memory cache
    public bool UseL2Cache { get; set; } = true; // L2 (e.g., Redis or persistent local)
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5); // General default
    public TimeSpan DefaultL1SlidingExpiration { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan DefaultL1Expiration { get; set; } = TimeSpan.FromMinutes(5); // Absolute for L1
    public TimeSpan DefaultL2Expiration { get; set; } = TimeSpan.FromMinutes(30); // Absolute for L2
    public int MaxL1CacheItems { get; set; } = 10000;
    public long MaxL1CacheSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public string CachePath { get; set; } = "cache"; // For persistent L2 if disk-based
    public bool CompressItems { get; set; } = false;
    public int CompressionThreshold { get; set; } = 1024; // 1KB

    // This property was a bit ambiguous in Ghost.Configuration.CachingConfiguration.
    // Let's ensure it reflects L1 state.
    public bool UseMemoryCache
    {
      get => UseL1Cache;
      set => UseL1Cache = value;
    }
  }

  public class ResilienceDataConfig // Was ResilienceConfiguration in Ghost.Configuration
  {
    public bool EnableRetry { get; set; } = true;
    public int RetryCount { get; set; } = 3;
    public int RetryBaseDelayMs { get; set; } = 100; // Base for exponential backoff perhaps
    public bool EnableCircuitBreaker { get; set; } = true;
    public int CircuitBreakerThreshold { get; set; } = 5; // Failures before breaking
    public int CircuitBreakerDurationMs { get; set; } = 30000; // Duration circuit stays open
    public int TimeoutMs { get; set; } = 30000; // Default operation timeout
    public bool EnableBulkhead { get; set; } = false;
    public int MaxConcurrency { get; set; } = 100;
    public int MaxQueueSize { get; set; } = 50;
  }

  public class SecurityDataConfig // Was SecurityConfiguration in Ghost.Configuration
  {
    public bool EnableEncryption { get; set; } = false;
    public string EncryptionKey { get; set; } = ""; // Base64 encoded typically
    public string EncryptionIV { get; set; } = ""; // Base64 encoded typically
    public string EncryptionAlgorithm { get; set; } = "AES";
    public bool EnableAudit { get; set; } = false;
    public bool EnableAccessControl { get; set; } = false;
    public string AuditLogPath { get; set; } = "logs/audit";
    public int AuditLogRetentionDays { get; set; } = 90;
  }

  public class ObservabilityDataConfig // Was ObservabilityConfiguration in Ghost.Configuration
  {
    public string LogLevel { get; set; } = "Information"; // Default log level
    public bool EnableConsoleLogging { get; set; } = true;
    public bool EnableFileLogging { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableTracing { get; set; } = false;
    public bool EnableHealthChecks { get; set; } = true;
    public string LogsPath { get; set; } = "logs"; // Can default from CoreConfig.LogsPath
    public int LogRetentionDays { get; set; } = 7;
    public int MetricsIntervalSeconds { get; set; } = 15;
    public int HealthCheckIntervalSeconds { get; set; } = 30;
    public bool EnableStructuredLogging { get; set; } = true;
    public double TracingSamplingRate { get; set; } = 0.1; // 10%
  }

  // --- Example existing simplified ModuleConfig derivatives (for the Modules dictionary) ---
  // These are your original, simpler config classes. They can remain for use in the
  // Modules dictionary IF they serve a purpose distinct from the detailed data layer configs above.
  // If their purpose was to configure parts of the data layer, they are now superseded by the
  // dedicated properties (Redis, Postgres, etc.) in GhostConfig.

  public class SimpleRedisConfig : ModuleConfigBase
  {
    public string ConnectionString { get; set; } = "localhost:6379";
    public int Database { get; set; } = 0;
    public bool UseSsl { get; set; } = false;
    public int MaxRetries { get; set; } = 3;
  }

  public class SimplePostgresConfig : ModuleConfigBase
  {
    public string ConnectionString { get; set; } = "Host=localhost;Database=ghost;";
    public int MaxPoolSize { get; set; } = 100;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
  }

  public class SimpleLocalCacheConfig : ModuleConfigBase // Was LocalCacheConfig
  {
    public string Path { get; set; } = "cache";
    public long MaxSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public int MaxItems { get; set; } = 10000;
    public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromHours(1);
  }

  public class SimpleLoggingConfig : ModuleConfigBase // Was LoggingConfig
  {
    public string LogsPath { get; set; } = "logs";
    public string OutputsPath { get; set; } = "outputs";
    public string LogLevel { get; set; } = "Information";
    public bool EnableConsole { get; set; } = true;
    public bool EnableFile { get; set; } = true;
    public int RetentionDays { get; set; } = 7;
  }
}
