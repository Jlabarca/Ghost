using Ghost.Commands;
using Ghost.Infrastructure;
using Ghost.Services;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Ghost
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            // Setup dependency injection
            var services = new ServiceCollection();

            // Register infrastructure services
            services.AddSingleton<ProcessRunner>();
            services.AddSingleton<ProjectGenerator>();
            services.AddSingleton<ConfigManager>();
            services.AddSingleton<GithubService>();

            // Register application services
            services.AddSingleton<AppRunner>();

            // Create registrar for Spectre.Console
            var registrar = new TypeRegistrar(services);

            // Create and configure the CLI application
            var app = new CommandApp(registrar);

            app.Configure(config =>
            {
                config.SetApplicationName("ghost");

                config.AddCommand<RunCommand>("run")
                        .WithDescription("Run a Ghost application from a repository")
                        .WithExample("run", "--url", "https://github.com/user/app", "--", "hello", "--name", "World");

                config.AddCommand<CreateCommand>("create")
                        .WithDescription("Create a new Ghost application")
                        .WithExample("create", "MyApp");

                config.AddCommand<AliasCommand>("alias")
                        .WithDescription("Manage aliases for Ghost applications")
                        .WithExample("alias", "--create", "myapp", "--url", "https://github.com/user/app")
                        .WithExample("alias", "--remove", "myapp");

                config.AddCommand<PushCommand>("push")
                        .WithDescription("Push a Ghost application to a remote git repository")
                        .WithExample("push", "--token", "my-github-token");
            });

            return app.Run(args);
        }
    }

    // Type registrar for Spectre.Console dependency injection
}