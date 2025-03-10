using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Ghost.Core.Config;
using Ghost.SDK;
using Ghost.Templates;

namespace Ghost.Father.CLI;

public class GhostFatherCLI : GhostServiceApp
{
    private readonly CommandApp _app;
    private readonly TypeRegistrar _registrar;

    public GhostFatherCLI(GhostConfig config = null)
    {
        ConfigureServices();
        _registrar = new TypeRegistrar(Services);
        _app = new CommandApp(_registrar);
        ConfigureCommands(_app);
    }

    private void ConfigureServices()
    {
        Services.AddSingleton(_ => new TemplateManager(Path.Combine(AppContext.BaseDirectory, "templates")));
        Services.AddSingleton(Services);

        // Register all commands
        CommandRegistry.RegisterServices(Services);

        G.LogDebug("Registered services:");
        foreach (var service in Services)
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