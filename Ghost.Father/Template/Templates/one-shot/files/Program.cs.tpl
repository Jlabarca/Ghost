using Ghost.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace {{ defaultNamespace }};

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            using var app = new App();
            return await app.ExecuteAsync(args);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }
    }
}

/// <summary>
/// A one-shot Ghost application that runs and exits.
/// </summary>
public class App : GhostApp
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public App()
    {
        // Configure as a one-shot app
        IsService = false;
        AutoRestart = false;  // No need to restart one-shot apps

        // Build configuration
        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("GHOST_ENV") ?? "Development"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Configure services
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        G.LogInfo("{{ safe_name }} initialized");
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Add configuration
        services.AddSingleton<IConfiguration>(_configuration);

        // Register your services here
        // services.AddTransient<IDataProcessor, DataProcessor>();
    }

    public override async Task RunAsync(IEnumerable<string> args)
    {
        G.LogInfo("{{ safe_name }} starting...");

        try
        {
            // Process command line arguments
            var arguments = args.ToArray();

            if (arguments.Length == 0 || (arguments.Length == 1 && (arguments[0] == "-h" || arguments[0] == "--help")))
            {
                ShowHelp();
                return;
            }

            // Example: Process input files if provided
            foreach (var arg in arguments)
            {
                if (File.Exists(arg))
                {
                    await ProcessFileAsync(arg);
                }
            }

            G.LogInfo("{{ safe_name }} completed successfully.");
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error executing {{ safe_name }}");
            throw;
        }
    }

    private void ShowHelp()
    {
        Console.WriteLine("{{ project_name }} - {{ defaultDescription }}");
        Console.WriteLine();
        Console.WriteLine("Usage: {{ project_name }} [options] [file1 file2 ...]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -h, --help    Show this help message");
        // Add more options here
    }

    private async Task ProcessFileAsync(string filePath)
    {
        G.LogInfo($"Processing file: {filePath}");
        // Implement your file processing logic here
        await Task.Delay(100); // Placeholder for actual processing
    }

    // Not called for one-shot apps, but good to implement for cleanup
    public override async Task StopAsync()
    {
        G.LogInfo("{{ safe_name }} shutting down...");

        // Cleanup resources if needed
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        await base.StopAsync();
    }
}