using Ghost.Core.Data;
using Ghost.Core.Monitoring;
using Ghost.Core.PM;
using Ghost.Father;
using Ghost.Father.Models;
namespace Ghost.Core.Storage;


public class GhostStorageOptions
{
    // Database Configuration
    public DatabaseType DatabaseType { get; set; } = DatabaseType.SQLite;
    public string? PostgresConnectionString { get; set; }
    public string? SQLitePath { get; set; }

    // Cache Configuration
    public bool UseRedis { get; set; }
    public string? RedisConnectionString { get; set; }
    public string? LocalCachePath { get; set; }

    // Schema Configuration
    public Type[]? EntityTypes { get; set; }

    // Performance Settings
    public int MaxPoolSize { get; set; } = 100;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool EnableDetailedErrors { get; set; } = false;

    // Cache Settings
    public TimeSpan DefaultCacheExpiry { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxCacheItems { get; set; } = 10000;

    // Schema Settings
    public bool AutoCreateSchema { get; set; } = true;
    public bool ValidateSchemaOnStartup { get; set; } = true;
}

// Usage example:
public static class GhostStorageDefaults
{
    public static readonly Type[] CoreEntityTypes = new[]
    {
        // Storage
        typeof(KeyValueStore),

        // Process Management
        typeof(ProcessInfo),
        typeof(ProcessMetrics),
        typeof(ProcessState),
        typeof(ProcessHealth),
        typeof(ProcessConfig),

        // Configuration
        typeof(ConfigEntry),

        // State Management
        typeof(StateEntry),


        // Logging
        typeof(LogEntry),

        // typeof(ConfigHistory),
        //
        // typeof(StateSnapshot),
        // typeof(LogMetrics),
        //
        // // Monitoring
        // typeof(MetricPoint),
        // typeof(MetricSeries),
        // typeof(HealthCheck),
        //
        // // Messaging
        // typeof(MessageQueue),
        // typeof(MessageSubscription),
        //
        // // Tasks
        // typeof(TaskDefinition),
        // typeof(TaskInstance),
        // typeof(TaskSchedule),
        //
        // // Security
        // typeof(SecurityKey),
        // typeof(AccessToken)
    };
}