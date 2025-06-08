using System.Reflection;
using Ghost.Templates;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
namespace Ghost.Father.CLI;

public class GhostFatherCLI : GhostApp
{
#region Private Fields

    private CommandApp _spectreCliApp;

#endregion

#region Disposal

    public override async ValueTask DisposeAsync()
    {
        G.LogDebug("Disposing GhostFatherCLI specific resources...");
        // Any CLI specific resources to dispose would go here.
        // The IServiceProvider and other base resources are handled by GhostApp.DisposeAsync().
        await base.DisposeAsync();
        G.LogInfo("GhostFatherCLI disposed.");
    }

#endregion

#region GhostApp Overrides

    /// <summary>
    ///     Configures CLI-specific services.
    ///     Base services (Config, Bus, Cache) are registered by GhostApp.ConfigureServicesBase.
    /// </summary>
    protected override void ConfigureServices(IServiceCollection services)
    {
        // Add template manager
        string? templatesPath = GetTemplatesPath();
        services.AddSingleton(_ => new TemplateManager(templatesPath));
        G.LogInfo($"TemplateManager registered with path: {templatesPath}");

        // Register all commands.
        // This assumes CommandRegistry has static methods to register commands
        // both with IServiceCollection (for DI) and IConfigurator (for Spectre).
        CommandRegistry.RegisterServices(services); // For DI in commands
        G.LogInfo("CLI commands registered with DI.");
    }

    /// <summary>
    ///     Main execution logic for the CLI.
    ///     Sets up and runs the Spectre.Console command application.
    /// </summary>
    public override async Task RunAsync(IEnumerable<string> args)
    {
        if (args == null)
        {
            throw new ArgumentNullException(nameof(args), "Arguments cannot be null for CLI execution.");
        }

        // Create Spectre.Console TypeRegistrar using the IServiceProvider from the base class
        // 'this.Services' is the IServiceProvider built by GhostApp
        SpectreServiceProviderAdapter? spectreTypeRegistrar = new SpectreServiceProviderAdapter(Services);

        _spectreCliApp = new CommandApp(spectreTypeRegistrar);
        ConfigureSpectreApp(_spectreCliApp);

        string[]? cleanArgs = CleanupArgs(args.ToArray());
        G.LogDebug($"CLI attempting to run with args: {string.Join(" ", cleanArgs)}");

        try
        {
            Environment.ExitCode = await _spectreCliApp.RunAsync(cleanArgs);
        }
        catch (CommandParseException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            if (ex.Pretty != null)
            {
                AnsiConsole.Write(ex.Pretty);
            }
            G.LogError(ex, "Spectre.Console command parsing error.");
            Environment.ExitCode = -1; // Or a specific error code for parsing errors
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex, ExceptionFormats.ShortenPaths);
            G.LogError(ex, "Error executing Spectre.Console command.");
            Environment.ExitCode = 1; // General error
        }
    }

#endregion

#region CLI Specific Methods

    /// <summary>
    ///     Configures the Spectre.Console command application (metadata, commands).
    /// </summary>
    private void ConfigureSpectreApp(CommandApp app)
    {
        app.Configure(config =>
        {
            Version? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            config.SetApplicationName("ghost");
            config.SetApplicationVersion($"{assemblyVersion?.Major ?? 1}.{assemblyVersion?.Minor ?? 0}.{assemblyVersion?.Build ?? 0}");

            CommandRegistry.ConfigureCommands(config); // For Spectre.Console command registration

            // config.PropagateExceptions(); // Good for debugging during development
            // config.ValidateExamples(); // If you use examples in command help
            // config.CaseSensitivity(CaseSensitivity.None); // Optional: make commands case-insensitive

        #if DEBUG
            config.PropagateExceptions();
            config.ValidateExamples();
        #endif
        });
    }

    /// <summary>
    ///     Determines the path to the templates directory.
    /// </summary>
    private string GetTemplatesPath()
    {
        // Check environment variable first
        string? ghostInstallDir = Environment.GetEnvironmentVariable("GHOST_INSTALL_DIR"); // Consistent naming
        if (!string.IsNullOrEmpty(ghostInstallDir))
        {
            string? templatesPath = Path.Combine(ghostInstallDir, "templates");
            if (Directory.Exists(templatesPath))
            {
                return templatesPath;
            }
        }

        string? exePath = Assembly.GetExecutingAssembly().Location;
        string? exeDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(exeDir)) // Should not happen but good to check
        {
            exeDir = Directory.GetCurrentDirectory();
        }

        // Check relative to executable
        string? templatePathNextToExe = Path.Combine(exeDir, "templates");
        if (Directory.Exists(templatePathNextToExe))
        {
            return templatePathNextToExe;
        }

        // Check in parent directories (useful for development scenarios)
        DirectoryInfo? parent = Directory.GetParent(exeDir);
        while (parent != null)
        {
            string? templatePathInParent = Path.Combine(parent.FullName, "templates");
            if (Directory.Exists(templatePathInParent))
            {
                return templatePathInParent;
            }
            parent = parent.Parent;
        }

        // Fall back to default next to executable and create it if it doesn't exist
        G.LogWarn($"Templates directory not found through probing. Defaulting to: {templatePathNextToExe}. It will be created if it doesn't exist.");
        try
        {
            Directory.CreateDirectory(templatePathNextToExe); // Ensure it exists
        }
        catch (Exception ex)
        {
            G.LogError(ex, $"Failed to create default templates directory at {templatePathNextToExe}.");
            // Potentially return a path within user's local app data as a last resort
            string? appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GhostCLI", "templates");
            G.LogWarn($"Falling back to templates path: {appDataPath}");
            Directory.CreateDirectory(appDataPath);
            return appDataPath;
        }
        return templatePathNextToExe;
    }

    /// <summary>
    ///     Cleans up command line arguments, removing the executable path if present.
    /// </summary>
    private string[] CleanupArgs(string[] args)
    {
        if (args.Length > 0)
        {
            string? firstArg = args[0];
            // More robust check for executable paths
            if (firstArg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                firstArg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                // Check if it's likely the entry assembly
                string? entryAssemblyLocation = Assembly.GetEntryAssembly()?.Location;
                if (entryAssemblyLocation != null &&
                    Path.GetFullPath(firstArg).Equals(Path.GetFullPath(entryAssemblyLocation), StringComparison.OrdinalIgnoreCase))
                {
                    return args.Skip(1).ToArray();
                }
            }
        }
        return args;
    }

#endregion
}
