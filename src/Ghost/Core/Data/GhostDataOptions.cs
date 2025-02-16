namespace Ghost.Core.Data;

public class GhostDataOptions
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
