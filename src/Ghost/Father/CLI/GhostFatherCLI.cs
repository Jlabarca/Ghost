using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using Ghost.Core.Config;
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

            config.PropagateExceptions();
        });
    }

    public override async Task RunAsync(IEnumerable<string> args)
    {
        //var args = GetCommandLineArgs(); // Helper method to get args
        G.LogDebug("Running CLI with args: {0}", string.Join(" ", args));
        await _app.RunAsync(args);
    }

    private string[] GetCommandLineArgs()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 0 && (args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                args[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            return args.Skip(1).ToArray();
        }
        return args;
    }
}