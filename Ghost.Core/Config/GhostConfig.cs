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
}

public class AppInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Version { get; set; }
}

public class CoreConfig
{
    // Added 'Type' property for one-shot vs service apps
    public string Type { get; set; } = "one-shot"; // Default to one-shot

    // Service name for display
    public string ServiceName { get; set; }

    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int ListenPort { get; set; } = 31337;
    public string Mode { get; set; } = "development";
    public string LogsPath { get; set; } = "logs";
    public string DataPath { get; set; } = "data";
    public string AppsPath { get; set; } = "apps";
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