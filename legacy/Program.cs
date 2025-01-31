// using Ghost;
// using Ghost.Legacy.Commands;
// using Ghost.Legacy.Infrastructure;
// using Ghost.Legacy.Infrastructure.Database;
// using Ghost.Legacy.Infrastructure.Monitoring;
// using Ghost.Legacy.Services;
// using Microsoft.Extensions.DependencyInjection;
// using Spectre.Console.Cli;
// public static class Program
// {
//     public static async Task<int> Main(string[] args)
//     {
//         // Setup dependency injection
//         var services = new ServiceCollection();
//
//         // Register infrastructure services
//         services.AddSingleton<GhostDatabase>();
//         services.AddSingleton<ConfigManager>();
//         services.AddSingleton<GhostLogger>();
//         services.AddSingleton<ProcessRunner>();
//         services.AddSingleton<GithubService>();
//         // Register application services
//         services.AddSingleton<ProjectGenerator>();
//         services.AddSingleton<AppRunner>();
//         services.AddSingleton<WorkspaceManager>();
//         services.AddGhostPersistence("ghost-father");
//
//         // Create registrar for Spectre.Console
//         var registrar = new TypeRegistrar(services,
//
//         // Create and configure the CLI application
//         var app = new CommandApp(registrar);
//
//         app.Configure(config =>
//         {
//             config.SetApplicationName("ghost");
//
//             config.AddCommand<RunCommand>("run")
//                 .WithDescription("Run a Ghost application from a repository")
//                 .WithExample("run", "--url", "https://github.com/user/app", "--", "hello", "--name", "World");
//
//             config.AddCommand<CreateCommand>("create")
//                 .WithDescription("Create a new Ghost application")
//                 .WithExample("create", "MyApp");
//
//             config.AddCommand<AliasCommand>("alias")
//                 .WithDescription("Manage aliases for Ghost applications")
//                 .WithExample("alias", "--create", "myapp", "--url", "https://github.com/user/app")
//                 .WithExample("alias", "--remove", "myapp");
//
//             config.AddCommand<PushCommand>("push")
//                 .WithDescription("Push a Ghost application to a remote git repository")
//                 .WithExample("push", "--token", "my-github-token");
//
//
//             config.AddCommand<InstallCommand>("install")
//                 .WithDescription("Install Ghost CLI system-wide")
//                 .WithExample("install", "--workspace", "/custom/path");
//         });
//
//         return await app.RunAsync(args);
//     }
// }