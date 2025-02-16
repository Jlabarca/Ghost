using Ghost.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using Ghost.SDK;

namespace Ghost.Father.CLI;

public class GhostFatherCLI : GhostApp
{
    private readonly CommandApp _app;
    private readonly IServiceCollection _services;
    private readonly TypeRegistrar _registrar;

    public GhostFatherCLI(GhostConfig config = null) : base(config)
    {
        _services = new ServiceCollection();
        ConfigureServices(_services);
        _registrar = new TypeRegistrar(_services);
        _app = new CommandApp(_registrar);
        ConfigureCommands(_app);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Register core services
        services.AddSingleton(Bus);
        services.AddSingleton(Data);
        services.AddSingleton(Config);
        services.AddSingleton(Metrics);

        // Register self for command access
        services.AddSingleton<IServiceCollection>(services);

        // Register all commands
        CommandRegistry.RegisterServices(services);

        // Debug log registered services
        G.LogDebug("Registered services:");
        foreach (var service in services)
        {
            G.LogDebug("  {0} -> {1}",
                service.ServiceType.Name,
                service.ImplementationType?.Name ?? "null");
        }
    }

    private void ConfigureCommands(CommandApp app)
    {
        app.Configure(config =>
        {
            // Set metadata
            config.SetApplicationName("ghost");
            config.SetApplicationVersion("1.0.0");

            // Register all commands from registry
            CommandRegistry.ConfigureCommands(config);
        });
    }

    public override async Task RunAsync()
    {
        try
        {
            await _app.RunAsync(Environment.GetCommandLineArgs());
        }
        catch (Exception ex)
        {
            // Enhanced error logging
            if (ex.Message.Contains("Could not resolve type"))
            {
                G.LogError("Dependency injection error:");
                var resolver = new TypeResolver(_services.BuildServiceProvider());
                try
                {
                    // Try to resolve to get detailed error
                    var type = Type.GetType(ex.Message.Split('\'')[1]);
                    if (type != null)
                    {
                        resolver.Resolve(type);
                    }
                }
                catch (InvalidOperationException resolveEx)
                {
                    G.LogError(resolveEx.Message);
                }
            }
            else
            {
                G.LogError(ex.Message);
            }
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            throw;
        }
    }
}