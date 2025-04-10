using Ghost.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace {{ project_name }};

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            // Create host to support service lifetime
            using var host = CreateHostBuilder(args).Build();

            // Register host with Ghost
            var app = host.Services.GetRequiredService<ServiceApp>();

            // Start app but don't block - let host handle lifetime
            await app.StartAsync();

            // Start host and wait for shutdown
            await host.RunAsync();

            // Cleanup
            await app.StopAsync();

            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Fatal error: {ex.Message}");
            Ghost.LogError(ex, "Service terminated unexpectedly");
            return 1;
        }
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostContext, config) =>
            {
                confiGhost.AddJsonFile("appsettings.json", optional: false)
                      .AddJsonFile($"appsettings.{hostContext.HostingEnvironment.EnvironmentName}.json", optional: true)
                      .AddEnvironmentVariables();
            })
            .ConfigureServices((hostContext, services) =>
            {
                services.AddSingleton<ServiceApp>();
                services.AddSingleton<IConfiguration>(hostContext.Configuration);

                // Register background services
                services.AddHostedService<WorkerService>();

                // Register your services
                ConfigureServices(services, hostContext.Configuration);
            });

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Register application services here
        // services.AddSingleton<IMyService, MyService>();
    }
}

/// <summary>
/// A long-running Ghost service application
/// </summary>
public class ServiceApp : GhostApp
{
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly CancellationTokenSource _cts = new();

    public ServiceApp(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;

        // Configure as a service
        IsService = true;
        AutoRestart = true;
        TickInterval = TimeSpan.FromSeconds(5);
        MaxRestartAttempts = 3;

        Ghost.LogInfo("{{ safe_name }} service initialized");
    }

    public override async Task RunAsync(IEnumerable<string> args)
    {
        Ghost.LogInfo("{{ safe_name }} service running");

        // Service runs continuously and processes events via OnTickAsync
        try
        {
            // Set up initial state, connections, etc.
            await InitializeCoreComponentsAsync();

            // Service will remain running until StopAsync is called
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown, no need to log
        }
        catch (Exception ex)
        {
            Ghost.LogError(ex, "Error in service main loop");
            throw;
        }
    }

    private async Task InitializeCoreComponentsAsync()
    {
        // Connect to databases, message buses, etc.
        Ghost.LogInfo("Initializing service components...");

        // Example: Connect to database
        // var connectionString = _configuration.GetConnectionString("DefaultConnection");
        // await _database.ConnectAsync(connectionString);

        await Task.CompletedTask;
    }

    protected override async Task OnTickAsync()
    {
        // Called periodically based on TickInterval
        // This is where you implement the periodic work for your service

        try
        {
            // Example: Process pending messages
            // await ProcessPendingMessagesAsync();

            // Example: Update metrics
            await Ghost.TrackMetricAsync("service.heartbeat", 1);

            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Ghost.LogError(ex, "Error in service tick");
            // Don't rethrow - we want the service to keep running
        }
    }

    public override async Task StopAsync()
    {
        Ghost.LogInfo("{{ safe_name }} service stoppinGhost...");

        // Cancel the main loop
        _cts.Cancel();

        // Cleanup resources
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _cts.Dispose();

        await base.StopAsync();
    }
}

/// <summary>
/// Background worker for the service
/// </summary>
public class WorkerService : BackgroundService
{
    private readonly IConfiguration _configuration;

    public WorkerService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Ghost.LogInfo("Worker service starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Perform background work
                // await ProcessWorkItemsAsync();

                // Example: track metrics
                await Ghost.TrackMetricAsync("worker.loop", 1);

                // Wait before next cycle
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                Ghost.LogError(ex, "Error in worker execution");
                // Wait a bit before retrying after an error
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        Ghost.LogInfo("Worker service stopping");
    }
}