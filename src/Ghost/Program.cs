using Microsoft.Extensions.DependencyInjection;
using Ghost.Core.Config;
using Ghost.Core.Storage;
using Ghost.Core.Storage.Cache;
using Ghost.Core.Storage.Database;
using Ghost.Father;
using Ghost.Infrastructure.Logging;
using Spectre.Console.Cli;

namespace Ghost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            // Set up initial options
            var options = new GhostOptions
            {
                SystemId = "ghost",
                UseRedis = false,
                UsePostgres = false,
                DataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ghost"
                )
            };

            // Configure services
            var services = new ServiceCollection();
            services.AddSingleton(options);
            ConfigureServices(services, options);

            await using var father = new GhostFather(options);
            // add GhostFather as any other ghost service
            await father.StartAsync();
            //services.AddSingleton(father);

            // Initialize CLI
            var app = new CommandApp(new TypeRegistrar(services));
            ConfigureCommands(app);

            // Run command
            return await app.RunAsync(args);
        }
        catch (Exception ex)
        {
            G.LogCritical("Fatal error in Ghost CLI", ex);
            return 1;
        }
    }

    private static void ConfigureServices(IServiceCollection services, GhostOptions options)
    {
        // Configure caching
        services.AddSingleton(options);

        ICache cache = options.UseRedis
            ? new RedisCache(options.RedisConnectionString)
            : new LocalCache(options.DataDirectory);

        services.AddSingleton(cache);

        services.AddGhostLogger(cache, config =>
        {
            config.RedisKeyPrefix = $"ghost:logs:{options.SystemId}";
            config.LogsPath = Path.Combine(options.DataDirectory, "logs");
            config.OutputsPath = Path.Combine(options.DataDirectory, "outputs");
        });

        // Configure database
        services.AddSingleton<IDatabaseClient>(sp =>
            options.UsePostgres
                ? new PostgresClient(options.PostgresConnectionString)
                : new SQLiteClient(Path.Combine(options.DataDirectory, "ghost.db")));

        // Core services
        services.AddSingleton<IGhostData, GhostData>();
        services.AddSingleton<IGhostBus, GhostBus>();
        services.AddSingleton<IGhostConfig, GhostConfig>();

        // Command handlers
        // TODO: Add command handlers (CreateCommand, RunCommand, etc.)
        // services.AddTransient<CreateCommandHandler>();
        // services.AddTransient<RunCommandHandler>();
        // services.AddTransient<MonitorCommandHandler>();

        // Process management
        services.AddSingleton<ProcessManager>();
        services.AddSingleton<HealthMonitor>();
        services.AddSingleton<StateManager>();

        /*
        Missing components to implement:

        1. Templates - Create Command
        - Template engine for project generation
        - Template providers (default, service, etc.)
        - Template discovery and loading

        2. Monitoring
        - Metrics collection and storage
        - Health check system
        - Process monitoring and recovery

        3. Plugin system
        - Plugin loading and management
        - Plugin hooks and events
        - Plugin configuration
        */

        // Add other necessary services...
    }

    private static void ConfigureCommands(CommandApp app)
    {
        // Basic commands
        app.Configure(config =>
        {
            /*
            TODO: Implement and register commands:

            1. Project commands
            - ghost create [name] --template [template]
            - ghost init
            - ghost add [component]
            - ghost remove [component]

            2. Runtime commands
            - ghost run [app]
            - ghost start [service]
            - ghost stop [service]
            - ghost restart [service]

            3. Monitoring commands
            - ghost monitor
            - ghost status
            - ghost logs
            - ghost metrics

            4. Configuration commands
            - ghost config set [key] [value]
            - ghost config get [key]
            - ghost config list

            5. Deployment commands
            - ghost deploy [env]
            - ghost rollback
            - ghost scale [n]

            6. Maintenance commands
            - ghost cleanup
            - ghost repair
            - ghost upgrade

            7. Development commands
            - ghost dev
            - ghost test
            - ghost build
            */

            // Register basic commands
            // config.AddCommand<CreateCommand>("create")
            //     .WithDescription("Create a new Ghost application")
            //     .WithExample(new[] { "create", "MyApp", "--template", "service" });
            //
            // config.AddCommand<RunCommand>("run")
            //     .WithDescription("Run a Ghost application")
            //     .WithExample(new[] { "run", "MyApp" });
            //
            // config.AddCommand<MonitorCommand>("monitor")
            //     .WithDescription("Monitor Ghost applications")
            //     .WithExample(new[] { "monitor" });

            // Add more commands...
        });
    }
}