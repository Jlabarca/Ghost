// Missing Types/Implementations:

// Infrastructure
// - IAutoMonitor implementation in AutoMonitor.cs needs ProcessMetrics collection
// - ProcessMetricsCollector class in ProcessInfo.cs needs implementation
// - RedisClient.SubscribeAsync() method implementation
// - Complete CoreAPI implementation
//
// // Services
// - Full ProjectGenerator service implementation
// - AppRunner service implementation for new architecture
// - AliasManager service implementation
// - IStateManager completion in StateManager


using Ghost.Commands;
using Ghost.SDK;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Ghost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Configure Ghost options
        var options = new GhostOptions
        {
            SystemId = "ghost-cli",
            RedisConnectionString = "localhost:6379",
            PostgresConnectionString = "Host=localhost;Database=ghost;Username=ghost;Password=ghost"
        };

        // Initialize Ghost Core
        var ghost = new GhostCore(opts =>
        {
            opts.SystemId = options.SystemId;
            opts.RedisConnectionString = options.RedisConnectionString;
            opts.PostgresConnectionString = options.PostgresConnectionString;
            opts.EnableMetrics = true;
        });

        // Setup services
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(p => ghost.Build());

        // Register commands
        services.AddTransient<RunCommand>();
        services.AddTransient<CreateCommand>();
        services.AddTransient<AliasCommand>();
        //services.AddTransient<MonitorCommand>();
        services.AddTransient<ConfigCommand>();

        // Build Spectre CLI
        var registrar = new TypeRegistrar(services);
        var app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.SetApplicationName("ghost");

            config.AddCommand<RunCommand>("run")
                .WithDescription("Run a Ghost application")
                .WithExample("run", "--url", "https://github.com/user/app");

            config.AddCommand<CreateCommand>("create")
                .WithDescription("Create a new Ghost application")
                .WithExample("create", "MyApp");

            config.AddCommand<AliasCommand>("alias")
                .WithDescription("Manage application aliases")
                .WithExample("alias", "--create", "myapp", "--url", "https://github.com/user/app");

            // config.AddCommand<MonitorCommand>("monitor")
            //     .WithDescription("Monitor running applications")
            //     .WithExample(new[] { "monitor" });

            config.AddCommand<ConfigCommand>("config")
                .WithDescription("Manage system configuration")
                .WithExample("config", "set", "githubToken", "token123");
        });

        return await app.RunAsync(args);
    }
}
