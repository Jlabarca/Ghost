using Ghost.Config;
namespace Ghost;

/// <summary>
/// Extensions to maintain compatibility with any existing code that might
/// expect the old configuration access patterns
/// </summary>
public static class GhostConfigExtensions
{
    /// <summary>
    /// Gets Redis connection string with fallback logic
    /// </summary>
    public static string? GetRedisConnectionString(this GhostConfig config)
    {
        // Priority: Direct Redis config > Environment variable > Core settings
        if (config.Redis.Enabled && !string.IsNullOrEmpty(config.Redis.ConnectionString))
            return config.Redis.ConnectionString;

        var envConnection = Environment.GetEnvironmentVariable("GHOST_REDIS_CONNECTION");
        if (!string.IsNullOrEmpty(envConnection))
            return envConnection;

        return config.Core.Settings.GetValueOrDefault("RedisConnection");
    }

    // public static string GetRedisConnectionString(this GhostConfig config)
    // {
    //     // Priority: Direct Redis config > Environment variable > Core settings
    //     if (config.Redis.Enabled && !string.IsNullOrEmpty(config.Redis.ConnectionString))
    //         return config.Redis.ConnectionString;
    //
    //     var envConnection = Environment.GetEnvironmentVariable("GHOST_REDIS_CONNECTION");
    //     if (!string.IsNullOrEmpty(envConnection))
    //         return envConnection;
    //
    //     config.Core.Settings?.TryGetValue("RedisConnection", out var settingsConnection);
    //     return settingsConnection;
    // }

    /// <summary>
    /// Gets PostgreSQL connection string with fallback logic
    /// </summary>
    public static string? GetPostgresConnectionString(this GhostConfig config)
    {
        // Priority: Direct Postgres config > Environment variable > Core settings
        if (config.Postgres.Enabled && !string.IsNullOrEmpty(config.Postgres.ConnectionString))
            return config.Postgres.ConnectionString;

        var envConnection = Environment.GetEnvironmentVariable("GHOST_POSTGRES_CONNECTION");
        if (!string.IsNullOrEmpty(envConnection))
            return envConnection;

        config.Core.Settings.TryGetValue("PostgresConnection", out var settingsConnection);
        return settingsConnection;
    }

    /// <summary>
    /// Checks if the application is in testing mode
    /// </summary>
    public static bool IsTestingMode(this GhostConfig config)
    {
        return config.Core.UseInMemoryDatabase ||
               config.Core.Mode?.Equals("testing", StringComparison.OrdinalIgnoreCase) == true ||
               Environment.GetEnvironmentVariable("GHOST_TESTING_MODE")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Gets PostgreSQL connection string with fallback logic
    /// </summary>
    // public static string? GetPostgresConnectionString(this GhostConfig config)
    // {
    //     // Priority: Direct Postgres config > Environment variable > Core settings
    //     if (config.Postgres.Enabled && !string.IsNullOrEmpty(config.Postgres.ConnectionString))
    //         return config.Postgres.ConnectionString;
    //
    //     var envConnection = Environment.GetEnvironmentVariable("GHOST_POSTGRES_CONNECTION");
    //     if (!string.IsNullOrEmpty(envConnection))
    //         return envConnection;
    //
    //     return config.Core.Settings.TryGetValue("PostgresConnection");
    // }

}
