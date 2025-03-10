// namespace Ghost.Core.Config;
//
// public class GhostOptions
// {
//   // System Settings
//   public string SystemId { get; set; } = "ghost";
//   public int Port { get; set; }
//   public string DataDirectory { get; set; }
//   public bool IsProduction { get; set; }
//
//   // Storage Settings
//   public bool UseRedis { get; set; }
//   public bool UsePostgres { get; set; }
//   public string RedisConnectionString { get; set; }
//   public string PostgresConnectionString { get; set; }
//
//   // Monitoring Settings
//   public bool EnableMetrics { get; set; } = true;
//   public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);
//   public int MetricsRetentionDays { get; set; } = 7;
//
//   // Logging Settings
//   public string LogLevel { get; set; } = "Information";
//   public bool FileLoggingEnabled { get; set; } = true;
//   public bool ConsoleLoggingEnabled { get; set; } = true;
//   public int LogRetentionDays { get; set; } = 30;
//
//   // Security Settings
//   public bool AllowRemoteConnections { get; set; }
//   public bool RequireAuthentication { get; set; }
//   public string AuthKey { get; set; }
//
//   // Additional Configuration
//   public Dictionary<string, string> AdditionalConfig { get; set; } = new();
//
//   public GhostOptions()
//   {
//     // Set default paths
//     DataDirectory = Path.Combine(
//         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
//         "Ghost"
//     );
//
//     // Default connections
//     RedisConnectionString = "localhost:6379";
//     PostgresConnectionString = "Host=localhost;Database=ghost;";
//
//     // Default port
//     Port = 31337;
//   }
//
//   public string GetLogsPath() => Path.Combine(DataDirectory, "logs");
//   public string GetDataPath() => Path.Combine(DataDirectory, "data");
//   public string GetAppsPath() => Path.Combine(DataDirectory, "apps");
//   public string GetConfigPath() => Path.Combine(DataDirectory, ".ghost.yaml");
// }
