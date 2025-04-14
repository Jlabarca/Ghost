using Ghost.Core.Modules;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ghost.Core.Config;

public class GhostConfig
{
    public AppInfo App { get; set; }
    public CoreConfig Core { get; set; }
    public Dictionary<string, ModuleConfig> Modules { get; set; } = new Dictionary<string, ModuleConfig>();
    public bool HasModule(string name) => Modules.ContainsKey(name) && Modules[name].Enabled;

    public T GetModuleConfig<T>(string name) where T : ModuleConfig =>
        HasModule(name) ? (T)Modules[name] : null;

    public static async Task<GhostConfig> LoadAsync(string path = ".ghost.yaml")
    {
        var yaml = await File.ReadAllTextAsync(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<GhostConfig>(yaml);
    }

    public string GetLogsPath()
    {
        return Path.Combine(App.Id, Core.LogsPath);
    }

    public string GetDataPath()
    {
        return Path.Combine(App.Id, Core.DataPath);
    }

    public string GetAppsPath()
    {
        return Path.Combine(App.Id, Core.AppsPath);
    }
    public string? ToYaml()
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        return serializer.Serialize(this);
    }
}

public class AppInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Version { get; set; }
}

    /// <summary>
    /// Core configuration settings for Ghost applications
    /// </summary>
    public class CoreConfig
    {
        /// <summary>
        /// Operation mode (development, production)
        /// </summary>
        public string Mode { get; set; } = "development";

        /// <summary>
        /// Path for application logs
        /// </summary>
        public string LogsPath { get; set; } = "logs";

        /// <summary>
        /// Path for application data storage
        /// </summary>
        public string DataPath { get; set; } = "data";

        /// <summary>
        /// Path for storing Ghost apps
        /// </summary>
        public string AppsPath { get; set; } = "ghosts";

        /// <summary>
        /// Port for the Ghost API to listen on (0 = auto-assign)
        /// </summary>
        public int ListenPort { get; set; } = 0;

        /// <summary>
        /// Interval for health checks
        /// </summary>
        public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Interval for metrics collection
        /// </summary>
        public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Additional settings dictionary for flexible configuration
        /// </summary>
        public Dictionary<string, string> Settings { get; set; } = new Dictionary<string, string>
        {
            ["autoGhostFather"] = "true",
            ["autoMonitor"] = "true"
        };

        /// <summary>
        /// Paths to watch for file changes (if enabled)
        /// </summary>
        public List<string> WatchPaths { get; set; } = new List<string>();

        /// <summary>
        /// File patterns to ignore when watching for changes
        /// </summary>
        public List<string> WatchIgnore { get; set; } = new List<string> { "*.log", "*.tmp" };

        /// <summary>
        /// GhostFather instance host (for remote connections)
        /// </summary>
        public string GhostFatherHost { get; set; } = "localhost";

        /// <summary>
        /// GhostFather instance port (for remote connections)
        /// </summary>
        public int GhostFatherPort { get; set; } = 5000;
    }

public class RedisConfig : ModuleConfig
{
    public string ConnectionString { get; set; } = "localhost:6379";
    public int Database { get; set; } = 0;
    public bool UseSsl { get; set; } = false;
    public int MaxRetries { get; set; } = 3;
}

public class PostgresConfig : ModuleConfig
{
    public string ConnectionString { get; set; } = "Host=localhost;Database=ghost;";
    public int MaxPoolSize { get; set; } = 100;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public class LocalCacheConfig : ModuleConfig
{
    public string Path { get; set; } = "cache";
    public long MaxSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
    public int MaxItems { get; set; } = 10000;
    public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromHours(1);
}

public class LoggingConfig : ModuleConfig
{
    public string LogsPath { get; set; } = "logs";
    public string OutputsPath { get; set; } = "outputs";
    public string LogLevel { get; set; } = "Information";
    public bool EnableConsole { get; set; } = true;
    public bool EnableFile { get; set; } = true;
    public int RetentionDays { get; set; } = 7;
}