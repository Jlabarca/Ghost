using Ghost.Commands;
using Ghost.Infrastructure.Templates;
using Ghost.SDK;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ghost;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            // Initialize Ghost SDK with GhostFather-specific configuration
            var ghost = new GhostCore(opts =>
            {
                opts.SystemId = "ghost-father";
                opts.UseRedis = false;  // Start with local cache for simplicity
                opts.UsePostgres = false; // Start with SQLite for simplicity
                opts.EnableMetrics = true;
                opts.MetricsInterval = TimeSpan.FromSeconds(5);

                // Optional: Override default data directory
                opts.DataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ghost"
                );

                // Add any GhostFather-specific configuration
                opts.AdditionalConfig["role"] = "manager";
            });

            // Build the Ghost application
            var app = ghost.Build();

            // Set up CLI-specific services
            var services = new ServiceCollection();
            services.AddSingleton(app);

            // Add Ghost infrastructure
            services.AddGhostInfrastructure(ghost.Options);

            // Register Ghost app and its core services for CLI commands to use
            services.AddSingleton(app);
            services.AddSingleton(app.API);
            services.AddSingleton(app.State);
            services.AddSingleton(app.Config);
            services.AddSingleton(app.Data);
            services.AddSingleton(app.Monitor);

            // Register template engine and project generator
            var templatesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates");
            services.AddSingleton(sp => new TemplateEngine(templatesPath));
            services.AddSingleton<ProjectGenerator>();

            // Register CLI commands
            services.AddTransient<StartCommand>();
            services.AddTransient<CreateCommand>();
            services.AddTransient<RunCommand>();
            services.AddTransient<AliasCommand>();
            services.AddTransient<ConfigCommand>();
            //services.AddTransient<MonitorCommand>();

            // Start the Ghost application
            await app.StartAsync();

            // Configure and run Spectre CLI
            var registrar = new TypeRegistrar(services);
            var cli = new CommandApp(registrar);

            cli.Configure(config =>
            {
                config.SetApplicationName("ghost");

                config.AddCommand<StartCommand>("start")
                    .WithDescription("Start a Ghost application")
                    .WithExample(new[] { "start" });

                config.AddCommand<CreateCommand>("create")
                    .WithDescription("Create a new Ghost application")
                    .WithExample(new[] { "create", "MyApp" });

                config.AddCommand<RunCommand>("run")
                    .WithDescription("Run a Ghost application")
                    .WithExample(new[] { "run", "myapp" });

                config.AddCommand<AliasCommand>("alias")
                    .WithDescription("Manage application aliases")
                    .WithExample(new[] { "alias", "--create", "myapp", "--url", "https://github.com/user/app" });

                config.AddCommand<ConfigCommand>("config")
                    .WithDescription("Manage system configuration")
                    .WithExample(new[] { "config", "set", "githubToken", "token123" });

                // config.AddCommand<MonitorCommand>("monitor")
                //     .WithDescription("Monitor Ghost processes")
                //     .WithExample(new[] { "monitor" });
            });

            // Run the CLI command
            var result = await cli.RunAsync(args);

            // Clean shutdown
            await app.StopAsync();

            return result;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal error:[/] {ex.Message}");
            if (ex.InnerException != null)
            {
                AnsiConsole.MarkupLine($"[grey]Caused by:[/] {ex.InnerException.Message}");
            }
            return 1;
        }
    }
}