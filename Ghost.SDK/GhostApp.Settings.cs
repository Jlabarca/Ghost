using Ghost.Config;
using System.Reflection;

namespace Ghost
{
    public abstract partial class GhostApp
    {
        #region Configuration Loading and Settings Application

        private GhostConfig? LoadConfigFromYaml()
        {
            try
            {
                string yamlPath = Path.Combine(Directory.GetCurrentDirectory(), ".ghost.yaml");
                if (File.Exists(yamlPath))
                {
                    G.LogInfo($"Loading config from: {yamlPath}");
                    return GhostConfig.LoadAsync(yamlPath).GetAwaiter().GetResult();
                }
                G.LogInfo(".ghost.yaml not found, will use default configuration.");
            }
            catch (Exception ex)
            {
                G.LogWarn($"Failed to load config from .ghost.yaml: {ex.Message}. Using defaults.");
            }

            return null; // Return null if not found or error, CreateDefaultConfig will be called
        }

        private GhostConfig CreateDefaultConfig()
        {
            var appType = GetType();
            var appNameAttr = appType.Name;
            var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant()
                                    ?? Path.GetFileNameWithoutExtension(Environment.ProcessPath)?.ToLowerInvariant()
                                    ?? "ghost_app";

            G.LogInfo($"Creating default configuration for App ID: {entryAssemblyName}, Name: {appNameAttr}");
            return new GhostConfig
            {
                App = new AppInfo
                {
                    Id = entryAssemblyName,
                    Name = appNameAttr,
                    Description = $"{appNameAttr} - A Ghost Application",
                    Version = appType.Assembly.GetName().Version?.ToString() ?? "1.0.0"
                },
                Core = new CoreConfig
                {
                    Mode = "development", // Default mode
                    LogsPath = Path.Combine(Directory.GetCurrentDirectory(), "logs"),
                    DataPath = Path.Combine(Directory.GetCurrentDirectory(), "data"),
                    Settings = new Dictionary<string, string>() // Ensure settings dict exists
                },
            };
        }

        private void ApplySettings()
        {
            // Apply from attributes first (as defaults)
            var attribute = GetType().GetCustomAttribute<GhostAppAttribute>();
            if (attribute != null)
            {
                IsService = attribute.IsService;
                AutoGhostFather = attribute.AutoGhostFather;
                AutoMonitor = attribute.AutoMonitor; // This is for GhostFatherConnection
                AutoRestart = attribute.AutoRestart;
                MaxRestartAttempts = attribute.MaxRestartAttempts;
                if (attribute.TickIntervalSeconds > 0)
                    TickInterval = TimeSpan.FromSeconds(attribute.TickIntervalSeconds);
            }

            // Override from loaded configuration
            if (Config?.Core?.Settings != null)
            {
                IsService = GetBoolSetting("isService", IsService);
                AutoGhostFather = GetBoolSetting("autoGhostFather", AutoGhostFather);
                AutoMonitor = GetBoolSetting("autoMonitor", AutoMonitor);
                AutoRestart = GetBoolSetting("autoRestart", AutoRestart);
                MaxRestartAttempts = GetIntSetting("maxRestartAttempts", MaxRestartAttempts);
                int tickSeconds = GetIntSetting("tickIntervalSeconds", (int)TickInterval.TotalSeconds);
                if (tickSeconds > 0) TickInterval = TimeSpan.FromSeconds(tickSeconds);
            }
            G.LogInfo($"Applied Settings: IsService={IsService}, AutoGhostFather={AutoGhostFather}, AutoMonitor={AutoMonitor}, AutoRestart={AutoRestart}, MaxRestarts={MaxRestartAttempts}, TickInterval={TickInterval.TotalSeconds}s");
        }

        // Helper methods to get typed settings with defaults
        protected bool GetBoolSetting(string name, bool defaultValue)
        {
            if (Config?.Core?.Settings?.TryGetValue(name, out var valueStr) == true)
            {
                if (bool.TryParse(valueStr, out bool result)) return result;
                // Handle "0" or "1" as bool for convenience
                if (int.TryParse(valueStr, out int intResult)) return intResult != 0;
            }
            return defaultValue;
        }

        protected int GetIntSetting(string name, int defaultValue)
        {
            if (Config?.Core?.Settings?.TryGetValue(name, out var valueStr) == true &&
                int.TryParse(valueStr, out int result))
            {
                return result;
            }
            return defaultValue;
        }

        protected string GetStringSetting(string name, string defaultValue)
        {
            return Config?.Core?.Settings?.TryGetValue(name, out var valueStr) == true ? valueStr : defaultValue;
        }

         /// <summary>
        /// Helper to get connection configuration details from GhostConfig.
        /// This was previously in GhostApp.Bus.cs
        /// </summary>
        private ConnectionConfiguration GetConnectionConfiguration(GhostConfig config)
        {
            var connConfig = new ConnectionConfiguration();
            string redisConnStr = Environment.GetEnvironmentVariable("GHOST_REDIS_CONNECTION");
            if (string.IsNullOrEmpty(redisConnStr) && config?.Core?.Settings?.TryGetValue("RedisConnection", out var confRedisConnStr) == true)
            {
                redisConnStr = confRedisConnStr;
            }
            // Default to localhost if still not set, but log a warning if it's being used for RedisBus/Cache
            if (string.IsNullOrEmpty(redisConnStr))
            {
                // We don't set a default here; RedisBus/Cache creation logic will handle the absence
                // and fall back to in-memory versions.
                G.LogDebug("No Redis connection string configured. In-memory fallbacks will be used for Bus/Cache if Redis is attempted.");
            }
            connConfig.RedisConnectionString = redisConnStr; // Can be null/empty

            // Other settings from GhostApp.Bus.cs, adapt as needed
            connConfig.EnableFallback = GetBoolSetting("EnableFallback", true); // For GhostFatherConnection
            connConfig.EnableDiagnostics = GetBoolSetting("EnableDiagnostics", true); // For GhostFatherConnection
            // ... any other relevant settings for connection/bus ...

            return connConfig;
        }

        // Re-define ConnectionConfiguration if it's specific to this context
        // or ensure it's accessible (e.g., from GhostApp.Connection.cs if defined there)
        private class ConnectionConfiguration
        {
            public string RedisConnectionString { get; set; }
            public bool EnableFallback { get; set; } // Used by GhostFatherConnection
            public bool EnableDiagnostics { get; set; } // Used by GhostFatherConnection
            // Add other relevant fields from the old ConnectionConfiguration
        }


        #endregion

    }
}
