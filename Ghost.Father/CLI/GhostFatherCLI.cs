using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Ghost.Core.Storage;
using Ghost.Core.Data;
using Ghost.Core.Data.Implementations;
using Ghost.Templates;
using System.Reflection;

namespace Ghost.Father.CLI;

public class GhostFatherCLI : GhostApp
{
    private CommandApp _app;
    private TypeRegistrar _registrar;
    private bool _servicesConfigured = false;

    /// <summary>
    /// Run the CLI with the provided arguments
    /// </summary>
    public override async Task RunAsync(IEnumerable<string> args)
    {
        if (args == null)
        {
            throw new ArgumentNullException(nameof(args), "Arguments cannot be null");
        }

        // Initialize services
        ConfigureServices();

        // Create type registrar for Spectre.Console
        _registrar = new TypeRegistrar(Services);

        // Create command app
        _app = new CommandApp(_registrar);

        // Configure commands
        ConfigureCommands(_app);

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

    private void ConfigureServices()
    {
        if (_servicesConfigured)
            return;

        if (Config == null)
        {
            throw new ArgumentNullException(nameof(Config), "GhostFather CLI requires a valid GhostConfig");
        }

        Services.AddSingleton(Config);
        Services.AddSingleton<IServiceCollection>(Services);

        var cachePath = Path.Combine(
                Config.Core.DataPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ghost"),
                "cache");
        Directory.CreateDirectory(cachePath);
        var cache = new MemoryCache(G.GetLogger());
        Services.AddSingleton<ICache>(cache);

        // Configure bus
        var bus = new GhostBus(cache);
        Services.AddSingleton<IGhostBus>(bus);

        // Add template manager
        var templatesPath = GetTemplatesPath();
        Services.AddSingleton(_ => new TemplateManager(templatesPath));

        // Register all commands from the registry
        CommandRegistry.RegisterServices(Services);

        _servicesConfigured = true;
    }


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
            //config.UseAnsi();
        });
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
        // try
        // {
        //     Ghost.Templates.TemplateSetup.EnsureTemplatesExist(exeDir);
        // }
        // catch (Exception ex)
        // {
        //     G.LogError(ex, "Failed to initialize templates");
        // }

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
            // Don't call Execute() directly - it might already be initialized
            if (State == GhostAppState.Created || State == GhostAppState.Stopped || State == GhostAppState.Failed)
            {
                Execute(args);
            }
            else
            {
                // Already initialized, just run the command
                await RunAsync(args);
            }
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
            if (Services.BuildServiceProvider() is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
            else if (Services.BuildServiceProvider() is IDisposable d)
            {
                d.Dispose();
            }

            await base.DisposeAsync();
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error disposing GhostFatherCLI");
        }
    }
}