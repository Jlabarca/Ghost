using System.Diagnostics;
using System.Reflection;
using Ghost;
using Ghost.Config;
using Ghost.Father.CLI;
using Ghost.Father.Daemon;
using Ghost.Logging;
using Microsoft.Extensions.Logging;
public static class GhostFatherEntry
{
    /// <summary>
    ///     Main entry point for the GhostFather application.
    ///     Determines whether to run in CLI or Daemon mode based on arguments.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        // Process arguments first (e.g., to remove executable path if present)
        string[] processedArgs = ProcessArguments(args);

        // --- CRITICAL: Initialize Logger and Cache FIRST ---
        // This must happen before ANY GhostApp methods are called
        await InitializeGhostFoundationAsync(processedArgs);

        // Basic argument parsing to determine mode
        bool isDaemonMode = processedArgs.Contains("--daemon");

        try
        {
            if (isDaemonMode)
            {
                G.LogInfo("Starting GhostFather in Daemon mode...");
                GhostFatherDaemon daemon = new GhostFatherDaemon();
                GhostConfig daemonConfig = await LoadDaemonConfigWithInheritanceAsync(processedArgs);
                Task daemonTask = daemon.ExecuteAsync(processedArgs, daemonConfig);

                CancellationTokenSource cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    G.LogInfo("Shutdown signal (Ctrl+C) received for daemon.");
                    e.Cancel = true;
                    cts.Cancel();
                };

                // Wait for daemon task or cancellation
                try
                {
                    await Task.WhenAny(daemonTask, Task.Delay(Timeout.Infinite, cts.Token));
                }
                catch (TaskCanceledException)
                {
                    G.LogDebug("Daemon wait loop cancelled by shutdown signal.");
                }

                if (cts.IsCancellationRequested && !daemonTask.IsCompleted)
                {
                    G.LogInfo("Attempting graceful shutdown of daemon...");
                    await daemon.StopAsync();
                }

                await daemonTask;
                await daemon.DisposeAsync();
                G.LogInfo("GhostFatherDaemon has shut down.");
                return Environment.ExitCode;
            }
            G.LogInfo("Starting GhostFather in CLI mode...");
            GhostFatherCLI cli = new GhostFatherCLI();
            GhostConfig cliConfig = await LoadCliConfigWithInheritanceAsync();
            await cli.ExecuteAsync(processedArgs, cliConfig);
            await cli.DisposeAsync();
            G.LogInfo("GhostFatherCLI execution finished.");
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            G.LogCritical($"Unhandled fatal error in GhostFatherEntry.Main: {ex}");
            Console.Error.WriteLine($"FATAL ERROR: {ex}");
            return -1;
        }
    }

    /// <summary>
    ///     Initialize the foundational Ghost services (Logger and Cache) before any GhostApp code runs.
    ///     This is critical because GhostApp.ConfigureServicesBase() depends on G.GetLogger() being available.
    /// </summary>
    private static async Task InitializeGhostFoundationAsync(string[] args)
    {
        try
        {
            // 1. Determine initial log level from arguments
            LogLevel initialLogLevel = LogLevel.Information;
            if (args.Contains("--debug") ||
                args.Contains("--verbose"))
            {
                initialLogLevel = LogLevel.Debug;
            }
            else if (args.Contains("--trace"))
            {
                initialLogLevel = LogLevel.Trace;
            }

            // 2. Create logger configuration
            GhostLoggerConfiguration loggerConfig = new GhostLoggerConfiguration
            {
                    LogsPath = "logs",
                    OutputsPath = Path.Combine("logs", "outputs"),
                    LogLevel = initialLogLevel
            };

            // 3. 4. Create cache and logger
            DefaultGhostLogger logger = new DefaultGhostLogger(loggerConfig);
            MemoryCache cache = new MemoryCache(logger);

            logger.SetCache(cache);

            // 5. Initialize G static class
            G.Initialize(logger);
            G.SetCache(cache);

            // 6. Now connect the logger to the cache for proper logging
            cache.SetLogger(logger);

            G.LogInfo("Ghost foundation services (Logger and Cache) initialized successfully.");
            G.LogDebug($"Initial log level set to: {initialLogLevel}");
            G.LogDebug($"Command line arguments: [{string.Join(", ", args)}]");
        }
        catch (Exception ex)
        {
            // If foundation initialization fails, we can only use Console
            Console.Error.WriteLine($"CRITICAL: Failed to initialize Ghost foundation services: {ex}");
            throw;
        }
    }

    /// <summary>
    ///     Processes command-line arguments, typically to remove the executable path if it's the first argument.
    /// </summary>
    /// <param name="args">The original command-line arguments.</param>
    /// <returns>The processed command-line arguments.</returns>
    private static string[] ProcessArguments(string[] args)
    {
        if (args.Length > 0)
        {
            string firstArg = args[0];
            // More robust check for executable paths
            if (firstArg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                firstArg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Check if it's likely the entry assembly
                string? entryAssemblyLocation = Assembly.GetEntryAssembly()?.Location;
                if (entryAssemblyLocation != null &&
                    Path.GetFullPath(firstArg).Equals(Path.GetFullPath(entryAssemblyLocation), StringComparison.OrdinalIgnoreCase))
                {
                    return args.Skip(1).ToArray();
                }
            }
        }
        return args;
    }

    /// <summary>
    ///     Loads daemon configuration with inheritance from base .ghost.yaml using the new merging system
    /// </summary>
    private static async Task<GhostConfig> LoadDaemonConfigWithInheritanceAsync(string[] args)
    {
        ExecutionEnvironment environment = DetectExecutionEnvironment();
        G.LogInfo($"Detected execution environment: {environment}");

        // Get configuration file paths
        string baseConfigPath = GetBaseConfigPath(environment);
        string daemonConfigPath = GetDaemonConfigPath(environment);

        G.LogDebug($"Base config path: {baseConfigPath}");
        G.LogDebug($"Daemon config path: {daemonConfigPath}");

        // Use the new Solution 1 approach - load base + override in one call
        GhostConfig mergedConfig = await GhostConfig.LoadAsync(baseConfigPath, daemonConfigPath);

        // If the merged config is completely empty, create a fallback
        if (IsConfigEmpty(mergedConfig))
        {
            G.LogWarn("No configuration loaded, using fallback daemon configuration");
            mergedConfig = CreateFallbackDaemonConfig();
        }

        // Apply command line argument overrides
        ApplyDaemonCommandLineOverrides(mergedConfig, args);

        G.LogInfo("Daemon configuration loaded and merged successfully");
        return mergedConfig;
    }

    /// <summary>
    ///     Loads CLI configuration with inheritance from base .ghost.yaml using the new merging system
    /// </summary>
    private static async Task<GhostConfig> LoadCliConfigWithInheritanceAsync()
    {
        ExecutionEnvironment environment = DetectExecutionEnvironment();
        G.LogInfo($"Detected execution environment: {environment}");

        // Get configuration file paths
        string baseConfigPath = GetBaseConfigPath(environment);
        string cliConfigPath = GetCliConfigPath(environment);

        G.LogDebug($"Base config path: {baseConfigPath}");
        G.LogDebug($"CLI config path: {cliConfigPath}");

        // Use the new Solution 1 approach - load base + override in one call
        GhostConfig mergedConfig = await GhostConfig.LoadAsync(baseConfigPath, cliConfigPath);

        // If the merged config is completely empty, create a fallback
        if (IsConfigEmpty(mergedConfig))
        {
            G.LogWarn("No configuration loaded, using fallback CLI configuration");
            mergedConfig = CreateFallbackCliConfig();
        }

        G.LogInfo("CLI configuration loaded and merged successfully");
        return mergedConfig;
    }

    /// <summary>
    ///     Checks if a configuration is essentially empty (all main sections are null)
    /// </summary>
    private static bool IsConfigEmpty(GhostConfig config)
    {
        return config.App?.Id == "ghost-app" && // Default value means likely empty
               config.Core?.Mode == "development" && // Default value means likely empty
               config.Redis?.ConnectionString == "localhost:6379" && // Default value
               config.Postgres?.ConnectionString == "Host=localhost;Database=ghost;" && // Default value
               !File.Exists(".ghost.yaml"); // And no base config file exists
    }

    /// <summary>
    ///     Gets the base .ghost.yaml configuration file path
    /// </summary>
    private static string GetBaseConfigPath(ExecutionEnvironment environment)
    {
        return environment switch
        {
                ExecutionEnvironment.Development => GetDevelopmentConfigPath(".ghost.yaml"),
                ExecutionEnvironment.Installed => GetInstalledConfigPath(".ghost.yaml"),
                _ => Path.Combine(Directory.GetCurrentDirectory(), ".ghost.yaml")
        };
    }

    /// <summary>
    ///     Gets the daemon configuration file path based on environment
    /// </summary>
    private static string GetDaemonConfigPath(ExecutionEnvironment environment)
    {
        return environment switch
        {
                ExecutionEnvironment.Development => GetDevelopmentConfigPath("daemon.ghost.yaml"),
                ExecutionEnvironment.Installed => GetInstalledConfigPath("daemon.ghost.yaml"),
                _ => Path.Combine(Directory.GetCurrentDirectory(), "daemon.ghost.yaml")
        };
    }

    /// <summary>
    ///     Gets the CLI configuration file path based on environment
    /// </summary>
    private static string GetCliConfigPath(ExecutionEnvironment environment)
    {
        return environment switch
        {
                ExecutionEnvironment.Development => GetDevelopmentConfigPath("cli.ghost.yaml"),
                ExecutionEnvironment.Installed => GetInstalledConfigPath("cli.ghost.yaml"),
                _ => Path.Combine(Directory.GetCurrentDirectory(), "cli.ghost.yaml")
        };
    }

    /// <summary>
    ///     Detects the current execution environment
    /// </summary>
    private static ExecutionEnvironment DetectExecutionEnvironment()
    {
        try
        {
            string? executablePath = Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrEmpty(executablePath))
            {
                G.LogWarn("Could not determine executable path");
                return ExecutionEnvironment.Unknown;
            }

            G.LogDebug($"Executable path: {executablePath}");

            // Check if running under JetBrains profiler (development)
            if (executablePath.Contains("JetBrains") ||
                Environment.GetCommandLineArgs().Any(arg => arg.Contains("JetBrains")))
            {
                G.LogDebug("Detected JetBrains profiler - Development environment");
                return ExecutionEnvironment.Development;
            }

            // Check if running from bin/Debug or bin/Release (development)
            if (executablePath.Contains(Path.Combine("bin", "Debug")) ||
                executablePath.Contains(Path.Combine("bin", "Release")) ||
                executablePath.Contains(@"bin\Debug") ||
                executablePath.Contains(@"bin\Release"))
            {
                G.LogDebug("Detected Debug/Release build path - Development environment");
                return ExecutionEnvironment.Development;
            }

            // Check if in a development source structure
            string? executableDir = Path.GetDirectoryName(executablePath);
            if (executableDir != null)
            {
                // Look for .csproj files or solution files in parent directories
                DirectoryInfo? current = new DirectoryInfo(executableDir);
                while (current != null && current.Parent != null)
                {
                    if (current.GetFiles("*.csproj").Any() ||
                        current.GetFiles("*.sln").Any() ||
                        current.GetDirectories().Any(d => d.Name == "Ghost.Core" || d.Name == "Ghost.SDK"))
                    {
                        G.LogDebug($"Detected source structure at {current.FullName} - Development environment");
                        return ExecutionEnvironment.Development;
                    }
                    current = current.Parent;
                }
            }

            // Check if in installation directory structure
            string? installDir = GetInstallationDirectory();
            if (installDir != null && executablePath.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
            {
                G.LogDebug($"Detected installation directory {installDir} - Installed environment");
                return ExecutionEnvironment.Installed;
            }

            G.LogDebug("Could not determine environment, defaulting to Installed");
            return ExecutionEnvironment.Installed;
        }
        catch (Exception ex)
        {
            G.LogError($"Error detecting environment: {ex.Message}");
            return ExecutionEnvironment.Unknown;
        }
    }

    /// <summary>
    ///     Gets the development configuration file path (next to GhostFatherEntry.cs)
    /// </summary>
    private static string GetDevelopmentConfigPath(string configFileName)
    {
        try
        {
            // Find the source directory containing this file
            Assembly? entryAssembly = Assembly.GetEntryAssembly();
            string? executablePath = entryAssembly?.Location ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrEmpty(executablePath))
            {
                throw new InvalidOperationException("Could not determine executable path");
            }

            string? searchDir = Path.GetDirectoryName(executablePath);

            // Search up the directory tree for the Ghost.Father project
            while (searchDir != null)
            {
                // Look for GhostFatherEntry.Run.cs or .csproj files to identify the project root
                if (File.Exists(Path.Combine(searchDir, "GhostFatherEntry.Run.cs")) ||
                    File.Exists(Path.Combine(searchDir, "Ghost.Father.csproj")))
                {
                    string configPath = Path.Combine(searchDir, configFileName);
                    G.LogDebug($"Development config path: {configPath}");
                    return configPath;
                }

                // Also check if config file exists at this level
                string potentialConfigPath = Path.Combine(searchDir, configFileName);
                if (File.Exists(potentialConfigPath))
                {
                    G.LogDebug($"Found development config: {potentialConfigPath}");
                    return potentialConfigPath;
                }

                searchDir = Directory.GetParent(searchDir)?.FullName;
            }

            // Fallback to current directory
            string fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), configFileName);
            G.LogDebug($"Development config fallback: {fallbackPath}");
            return fallbackPath;
        }
        catch (Exception ex)
        {
            G.LogError($"Error finding development config path: {ex.Message}");
            return Path.Combine(Directory.GetCurrentDirectory(), configFileName);
        }
    }

    /// <summary>
    ///     Gets the installed configuration file path
    /// </summary>
    private static string GetInstalledConfigPath(string configFileName)
    {
        string? installDir = GetInstallationDirectory();
        if (installDir != null)
        {
            string configPath = Path.Combine(installDir, configFileName);
            G.LogDebug($"Installed config path: {configPath}");
            return configPath;
        }

        string fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), configFileName);
        G.LogDebug($"Installed config fallback: {fallbackPath}");
        return fallbackPath;
    }

    /// <summary>
    ///     Gets the installation directory
    /// </summary>
    private static string? GetInstallationDirectory()
    {
        // Check environment variable first
        string? installDir = Environment.GetEnvironmentVariable("GHOST_INSTALL");
        if (!string.IsNullOrEmpty(installDir) && Directory.Exists(installDir))
        {
            return installDir;
        }

        // Try to determine from executable location
        string? executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(executablePath))
        {
            string? executableDir = Path.GetDirectoryName(executablePath);
            if (executableDir != null)
            {
                // Check if we're in a bin subdirectory of an installation
                if (Path.GetFileName(executableDir).Equals("bin", StringComparison.OrdinalIgnoreCase))
                {
                    string? parentDir = Directory.GetParent(executableDir)?.FullName;
                    if (parentDir != null &&
                        (Directory.Exists(Path.Combine(parentDir, "templates")) ||
                         Directory.Exists(Path.Combine(parentDir, "libs"))))
                    {
                        return parentDir;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Applies command line argument overrides to daemon configuration
    /// </summary>
    private static void ApplyDaemonCommandLineOverrides(GhostConfig config, string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string currentArg = args[i];
            string? nextArg = i + 1 < args.Length ? args[i + 1] : null;

            switch (currentArg.ToLowerInvariant())
            {
                case "--redis":
                    if (nextArg != null)
                    {
                        config.Redis!.ConnectionString = nextArg;
                        config.Redis.Enabled = true;
                        G.LogInfo("Redis connection string overridden from command line");
                        i++;
                    }
                    break;

                case "--postgres":
                    if (nextArg != null)
                    {
                        config.Postgres!.ConnectionString = nextArg;
                        config.Postgres.Enabled = true;
                        G.LogInfo("PostgreSQL connection string overridden from command line");
                        i++;
                    }
                    break;

                case "--no-redis":
                    config.Redis!.Enabled = false;
                    G.LogInfo("Redis disabled via command line");
                    break;

                case "--no-postgres":
                    config.Postgres!.Enabled = false;
                    G.LogInfo("PostgreSQL disabled via command line");
                    break;

                case "--data-path":
                    if (nextArg != null)
                    {
                        config.Core!.DataPath = nextArg;
                        G.LogInfo($"Data path overridden from command line: {nextArg}");
                        i++;
                    }
                    break;

                case "--logs-path":
                    if (nextArg != null)
                    {
                        config.Core!.LogsPath = nextArg;
                        G.LogInfo($"Logs path overridden from command line: {nextArg}");
                        i++;
                    }
                    break;

                case "--port":
                    if (nextArg != null && int.TryParse(nextArg, out int port))
                    {
                        config.Core!.ListenPort = port;
                        G.LogInfo($"Listen port overridden from command line: {port}");
                        i++;
                    }
                    break;

                case "--development":
                    config.Core!.Mode = "development";
                    G.LogInfo("Mode set to development from command line");
                    break;
            }
        }
    }

    /// <summary>
    ///     Creates a fallback daemon configuration when YAML loading fails
    /// </summary>
    private static GhostConfig CreateFallbackDaemonConfig()
    {
        G.LogWarn("Using fallback daemon configuration");

        GhostConfig config = new GhostConfig();

        // Use the static factory methods to get proper defaults
        config.App = AppInfo.CreateDefault();
        config.App.Id = "ghost-daemon";
        config.App.Name = "GhostFather Daemon";
        config.App.Description = "Ghost Process Manager Daemon";
        config.App.Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

        config.Core = CoreConfig.CreateDefault();
        config.Core.Mode = "production";
        config.Core.LogsPath = Path.Combine(Directory.GetCurrentDirectory(), "logs", "daemon");
        config.Core.DataPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "daemon");
        config.Core.UseInMemoryDatabase = false;
        config.Core.Settings = new Dictionary<string, string>
        {
                ["isService"] = "true",
                ["autoGhostFather"] = "false",
                ["autoMonitor"] = "true",
                ["autoRestart"] = "false",
                ["maxRestarts"] = "0",
                ["tickInterval"] = "5s"
        };

        config.Redis = RedisDataConfig.CreateDefault();
        config.Redis.Enabled = true;
        config.Redis.Database = 1;

        config.Postgres = PostgresDataConfig.CreateDefault();
        config.Postgres.Enabled = false; // Disable by default in fallback to avoid connection errors
        config.Postgres.ConnectionString = "Host=localhost;Port=5432;Database=ghostfather_db;Username=postgres;";

        config.Caching = CachingDataConfig.CreateDefault();
        config.Resilience = ResilienceDataConfig.CreateDefault();
        config.Resilience.RetryCount = 5;
        config.Observability = ObservabilityDataConfig.CreateDefault();

        return config;
    }

    /// <summary>
    ///     Creates a fallback CLI configuration when YAML loading fails
    /// </summary>
    private static GhostConfig CreateFallbackCliConfig()
    {
        G.LogWarn("Using fallback CLI configuration");

        GhostConfig config = new GhostConfig();

        // Use the static factory methods to get proper defaults
        config.App = AppInfo.CreateDefault();
        config.App.Id = "ghost-cli";
        config.App.Name = "Ghost CLI";
        config.App.Description = "Ghost Command Line Interface";
        config.App.Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

        config.Core = CoreConfig.CreateDefault();
        config.Core.Mode = "production";
        config.Core.Settings = new Dictionary<string, string>
        {
                ["autoGhostFather"] = "false",
                ["autoMonitor"] = "false"
        };

        config.Redis = RedisDataConfig.CreateDefault();
        config.Redis.Enabled = false;

        config.Postgres = PostgresDataConfig.CreateDefault();
        config.Postgres.Enabled = false;

        config.Caching = CachingDataConfig.CreateDefault();
        config.Caching.UseL2Cache = false;

        config.Resilience = ResilienceDataConfig.CreateDefault();
        config.Security = SecurityDataConfig.CreateDefault();
        config.Observability = ObservabilityDataConfig.CreateDefault();

        return config;
    }

    /// <summary>
    ///     Execution environment types
    /// </summary>
    private enum ExecutionEnvironment
    {
        Development,
        Installed,
        Unknown
    }
}
