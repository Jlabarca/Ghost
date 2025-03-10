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

public class App : GhostApp
{
    public App()
    {
        // Configure as a service
        IsService = true;
        TickInterval = TimeSpan.FromSeconds(5);
        AutoRestart = true;
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        // Add configuration
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("GHOST_ENV") ?? "Development"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        services.AddSingleton<IConfiguration>(config);

        // Add your services here
        // services.AddSingleton<IMyService, MyService>();

        base.ConfigureServices(services);
    }

    public override async Task RunAsync(IEnumerable<string> args)
    {
        G.LogInfo("{{ safe_name }} service starting...");
        await Task.CompletedTask;
    }

    protected override async Task OnTickAsync()
    {
        // Regular service work
        G.LogDebug("Service heartbeat");
        await Task.CompletedTask;
    }

    protected override async Task OnAfterRunAsync()
    {
        G.LogInfo("Service shutting down...");
        await Task.CompletedTask;
    }
}