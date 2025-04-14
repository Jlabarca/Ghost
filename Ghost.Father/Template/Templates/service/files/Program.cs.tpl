using Ghost.SDK;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using G = Ghost.Ghost;

namespace {{ project_name }};

/// <summary>
/// Main program class for service6 service
/// </summary>
public class Program
{
    /// <summary>
    /// Application entry point
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            // Set up console handling for graceful shutdown
            Console.Title = "{{ project_name }}";
            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true;
                Console.WriteLine("Shutdown requested. Please wait...");
            };

            G.LogInfo("Starting {{ project_name }} service...");

            // Create and configure the service
            var service = new Service6Service();

            // Run the service until shutdown is requested
            await service.ExecuteAsync(args);

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            return 1;
        }
    }
}

/// <summary>
/// Long-running service application
/// </summary>
[GhostApp(IsService = true, AutoMonitor = true, AutoRestart = true, TickIntervalSeconds = 10)]
public class Service6Service : GhostServiceApp
{
    private readonly IConfiguration _configuration;
    private IServiceProvider _serviceProvider;
    private int _uptimeSeconds = 0;

    /// <summary>
    /// Service constructor
    /// </summary>
    public Service6Service() : base()
    {
        // Build configuration
        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("GHOST_ENV") ?? "Development"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Register state and error events
        StateChanged += HandleStateChanged;
        ErrorOccurred += HandleError;

        G.LogInfo("service6 service initialized");
    }

    /// <summary>
    /// Configure services for dependency injection
    /// </summary>
    protected override void ConfigureServices()
    {
        // Add base services first
        base.ConfigureServices();

        // Add configuration
        Services.AddSingleton(_configuration);

        // Register your services here
        // Services.AddSingleton<IMyBackgroundService, MyBackgroundService>();

        // Build service provider
        _serviceProvider = Services.BuildServiceProvider();
    }

    /// <summary>
    /// Handle service state changes
    /// </summary>
    private void HandleStateChanged(object sender, GhostAppState state)
    {
        var stateName = state.ToString().ToUpper();

        // Set console color based on state
        Console.ForegroundColor = state switch {
            GhostAppState.Running => ConsoleColor.Green,
            GhostAppState.Stopping => ConsoleColor.Yellow,
            GhostAppState.Failed => ConsoleColor.Red,
            _ => ConsoleColor.White
        };

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Service state: {stateName}");
        Console.ResetColor();
    }

    /// <summary>
    /// Handle service errors
    /// </summary>
    private void HandleError(object sender, Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"Service error: {ex.Message}");
        Console.ResetColor();
    }

    /// <summary>
    /// Main service execution
    /// </summary>
    public override async Task RunAsync(IEnumerable<string> args)
    {
        G.LogInfo("{{ project_name }} service running...");

        // Initialize service components
        await InitializeServiceComponentsAsync();

        // Run the base service implementation which will handle the service loop
        await base.RunAsync(args);
    }

    /// <summary>
    /// Initialize service components
    /// </summary>
    private async Task InitializeServiceComponentsAsync()
    {
        // Initialize any background tasks or components
        try
        {
            // TODO: Initialize your service components here

            // Example: Report startup status
            var process = Process.GetCurrentProcess();
            await G.TrackMetricAsync("process.startup.memory", process.WorkingSet64);
            await G.TrackMetricAsync("process.startup.threads", process.Threads.Count);

            G.LogInfo("Service components initialized successfully");
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to initialize service components");
            throw; // Let the framework handle restart
        }
    }

    /// <summary>
    /// Service-specific tick processing - called at regular intervals
    /// </summary>
    protected override async Task ServiceTickAsync()
    {
        // Increment uptime counter
        _uptimeSeconds += (int)TickInterval.TotalSeconds;

        // Report basic metrics
        var process = Process.GetCurrentProcess();
        await G.TrackMetricAsync("service.uptime.seconds", _uptimeSeconds);
        await G.TrackMetricAsync("service.memory.mb", process.WorkingSet64 / 1024.0 / 1024.0);

        // TODO: Add your periodic service processing here
        // For example, check queues, process background tasks, etc.

        G.LogDebug($"Service heartbeat - uptime: {TimeSpan.FromSeconds(_uptimeSeconds)}");
    }

    /// <summary>
    /// Clean up resources
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        G.LogInfo("Disposing service6 service resources...");

        // TODO: Dispose your service resources here

        // Dispose the service provider if needed
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        // Let base class handle the rest
        await base.DisposeAsync();
    }
}