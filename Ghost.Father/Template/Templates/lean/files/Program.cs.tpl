using Ghost;
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
            await app.ExecuteAsync(args);
            return 0;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Application terminated unexpectedly");
            return 1;
        }
    }
}

public class App : GhostApp, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public App()
    {
        // Configure as a service
        IsService = true;

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

        G.LogInfo("{{ safe_name }} service initialized");
    }

    protected virtual void ConfigureServices(IServiceCollection services)
    {
        // Add configuration
        services.AddSingleton<IConfiguration>(_configuration);

        // Register your services here
        // services.AddSingleton<IMyService, MyService>();
    }

    public override async Task RunAsync(IEnumerable<string> args)
    {
        G.LogInfo("{{ safe_name }} service starting...");
        await Task.CompletedTask;
    }

    public void Dispose()
    {
        G.LogInfo("{{ safe_name }} service shutting down...");

        // Dispose services if needed
        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}