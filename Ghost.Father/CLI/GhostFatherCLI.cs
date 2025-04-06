using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Ghost.Core.Config;
using Ghost.Core.Storage;
using Ghost.Core.Logging;
using Ghost.Core.Data;
using Ghost.Templates;
using Ghost.Father.CLI.Commands;
using Ghost.SDK;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Ghost.Father.CLI
{
    /// <summary>
    /// Main CLI application for Ghost framework
    /// </summary>
    public class GhostFatherCLI : GhostServiceApp
    {
        private readonly CommandApp _app;
        private readonly TypeRegistrar _registrar;
        private readonly GhostConfig _config;

        /// <summary>
        /// Initialize a new instance of the CLI
        /// </summary>
        /// <param name="config">Optional configuration override</param>
        public GhostFatherCLI(GhostConfig config = null)
        {
            _config = config ?? LoadDefaultConfig();

            // Initialize services
            ConfigureServices();

            // Create type registrar for Spectre.Console
            _registrar = new TypeRegistrar(Services);

            // Create command app
            _app = new CommandApp(_registrar);

            // Configure commands
            ConfigureCommands(_app);

            G.LogInfo("GhostFather CLI initialized");
        }

        /// <summary>
        /// Configure the service collection with all required services
        /// </summary>
        private void ConfigureServices()
        {
            Services.AddSingleton(_config);
            Services.AddSingleton<IServiceCollection>(Services);

            // Add core services
            ConfigureCoreServices();

            // Add template manager
            string templatesPath = GetTemplatesPath();
            Services.AddSingleton(_ => new TemplateManager(templatesPath));

            // Register all commands from the registry
            CommandRegistry.RegisterServices(Services);

            if (G.IsDebug)
            {
                G.LogDebug("Registered services:");
                foreach (var service in Services)
                {
                    G.LogDebug($" {service.ServiceType.Name} -> {service.ImplementationType?.Name ?? "null"}");
                }
            }
        }

        /// <summary>
        /// Configure essential core services
        /// </summary>
        private void ConfigureCoreServices()
        {
            // Configure cache
            var cachePath = Path.Combine(
                _config.Core.DataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ghost"),
                "cache");

            Directory.CreateDirectory(cachePath);
            var cache = new LocalCache(cachePath);
            Services.AddSingleton<ICache>(cache);

            // Configure bus
            var bus = new GhostBus(cache);
            Services.AddSingleton<IGhostBus>(bus);

            // Configure database
            var dbPath = Path.Combine(
                _config.Core.DataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ghost"),
                "ghost.db");
            var database = new SQLiteDatabase(dbPath);
            Services.AddSingleton<IGhostData>(database);

            // Configure logger
            var loggerConfig = new GhostLoggerConfiguration
            {
                LogsPath = _config.Core.LogsPath ?? "logs",
                OutputsPath = Path.Combine(_config.Core.LogsPath ?? "logs", "outputs"),
                LogLevel = Microsoft.Extensions.Logging.LogLevel.Information
            };

            var logger = new SpectreGhostLogger(cache, loggerConfig);
            Services.AddSingleton<IGhostLogger>(logger);
            G.Initialize(logger);
        }

        /// <summary>
        /// Configure all commands in the Spectre.Console CLI app
        /// </summary>
        private void ConfigureCommands(CommandApp app)
        {
            app.Configure(config =>
            {
                // Set metadata
                var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
                config.SetApplicationName("ghost");
                config.SetApplicationVersion($"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}");

                // Register all commands from registry
                CommandRegistry.ConfigureCommands(config);

                // Enable exception propagation for better error handling
                config.PropagateExceptions();

                // Set custom style for console output
                config.UseAnsi();
            });
        }

        /// <summary>
        /// Run the CLI with the provided arguments
        /// </summary>
        public override async Task RunAsync(IEnumerable<string> args)
        {
            G.LogDebug($"Running CLI with args: {string.Join(" ", args)}");

            try
            {
                // Fix args if needed
                var cleanArgs = CleanupArgs(args);

                // Run the command app
                await _app.RunAsync(cleanArgs);
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Error executing command");
                throw;
            }
        }

        /// <summary>
        /// Load the default configuration
        /// </summary>
        private GhostConfig LoadDefaultConfig()
        {
            var ghostInstallDir = Environment.GetEnvironmentVariable("GHOST_INSTALL");

            return new GhostConfig
            {
                App = new AppInfo
                {
                    Id = "ghost-cli",
                    Name = "Ghost CLI",
                    Description = "Ghost Command Line Interface",
                    Version = Assembly.GetExecutingAssembly().GetName().Version.ToString()
                },
                Core = new CoreConfig
                {
                    Mode = "development",
                    LogsPath = string.IsNullOrEmpty(ghostInstallDir) ? "logs" : Path.Combine(ghostInstallDir, "logs"),
                    DataPath = string.IsNullOrEmpty(ghostInstallDir) ? "data" : Path.Combine(ghostInstallDir, "data"),
                    AppsPath = string.IsNullOrEmpty(ghostInstallDir) ? "ghosts" : Path.Combine(ghostInstallDir, "ghosts"),
                    HealthCheckInterval = TimeSpan.FromSeconds(30),
                    MetricsInterval = TimeSpan.FromSeconds(5)
                },
                Modules = new Dictionary<string, object>()
            };
        }

        /// <summary>
        /// Get the path to the templates directory
        /// </summary>
        private string GetTemplatesPath()
        {
            // Check environment variable first
            var ghostInstallDir = Environment.GetEnvironmentVariable("GHOST_INSTALL");
            if (!string.IsNullOrEmpty(ghostInstallDir))
            {
                var templatesPath = Path.Combine(ghostInstallDir, "templates");
                if (Directory.Exists(templatesPath))
                {
                    return templatesPath;
                }
            }

            // Check next to the executable
            var exePath = Assembly.GetExecutingAssembly().Location;
            var exeDir = Path.GetDirectoryName(exePath);
            var templatePathNext = Path.Combine(exeDir, "templates");
            if (Directory.Exists(templatePathNext))
            {
                return templatePathNext;
            }

            // Check parent directories
            var parent = Directory.GetParent(exeDir);
            while (parent != null)
            {
                var templatePathParent = Path.Combine(parent.FullName, "templates");
                if (Directory.Exists(templatePathParent))
                {
                    return templatePathParent;
                }
                parent = parent.Parent;
            }

            // Fall back to default next to executable and create it
            G.LogWarn($"Templates directory not found. Creating at: {templatePathNext}");
            Directory.CreateDirectory(templatePathNext);

            // Initialize templates
            try
            {
                Ghost.Templates.TemplateSetup.EnsureTemplatesExist(exeDir);
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Failed to initialize templates");
            }

            return templatePathNext;
        }

        /// <summary>
        /// Clean up command line arguments
        /// </summary>
        private string[] CleanupArgs(IEnumerable<string> args)
        {
            // If running from dotnet or directly, the first arg might be the executable
            var argArray = args.ToArray();
            if (argArray.Length > 0 &&
                (argArray[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                 argArray[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                return argArray.Skip(1).ToArray();
            }

            return argArray;
        }

        /// <summary>
        /// Execute the CLI with the provided arguments
        /// </summary>
        public async Task<int> ExecuteAsync(string[] args)
        {
            try
            {
                await RunAsync(args);
                return 0;
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Error executing CLI");
                return 1;
            }
        }

        /// <summary>
        /// Clean up resources
        /// </summary>
        public override async ValueTask DisposeAsync()
        {
            try
            {
                G.LogDebug("Disposing GhostFatherCLI");

                // Dispose all services
                if (Services is ServiceProvider sp)
                {
                    await sp.DisposeAsync();
                }

                await base.DisposeAsync();
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Error disposing GhostFatherCLI");
            }
        }
    }
}