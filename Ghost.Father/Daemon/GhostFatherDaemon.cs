using Ghost.Storage;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Ghost.Father.Daemon;

public class GhostFatherDaemon : GhostApp
{
    #region Private Fields

  private ProcessManager _processManager;
  private HealthMonitor _healthMonitor;
  private CommandProcessor _commandProcessor;
  private StateManager _stateManager;
  private AppCommunicationServer _communicationServer;
  private Timer _daemonMetricsReportingTimer;
  private readonly string _daemonId = "ghost-daemon";

    #endregion

    #region GhostApp Overrides

  /// <summary>
  /// Configures Daemon-specific services.
  /// Base services (Config, Bus, Cache) are registered by GhostApp.ConfigureServicesBase.
  /// </summary>
  protected override void ConfigureServices(IServiceCollection services)
  {
    services.AddSingleton<StateManager>(); // Depends on ICache (or IGhostData if you had it)
    services.AddSingleton<HealthMonitor>(); // Depends on IGhostBus
    services.AddSingleton<CommandProcessor>(); // Depends on IGhostBus
    services.AddSingleton<AppCommunicationServer>(); // Depends on IGhostBus, HealthMonitor, StateManager
    services.AddSingleton<ProcessManager>(); // Depends on IServiceProvider to resolve other services or config

    G.LogInfo("Daemon-specific services configured.");
  }

  public override async Task RunAsync(IEnumerable<string> args)
  {
    G.LogInfo("GhostFatherDaemon main execution starting...");

    // Resolve services after DI container is built by the base class
    _stateManager = Services.GetRequiredService<StateManager>();
    _healthMonitor = Services.GetRequiredService<HealthMonitor>();
    _commandProcessor = Services.GetRequiredService<CommandProcessor>();
    _communicationServer = Services.GetRequiredService<AppCommunicationServer>();
    _processManager = Services.GetRequiredService<ProcessManager>();

    G.LogInfo($"Daemon Bus Type: {Bus.GetType().FullName}");
    if (Bus is RedisGhostBus) // Check actual type
    {
      bool busAvailable = await Bus.IsAvailableAsync();
      G.LogInfo($"RedisGhostBus availability: {busAvailable}");
    }

    await InitializeDaemonServicesAsync();

    // The daemon should not try to connect to a "GhostFather" itself.
    // AutoGhostFather should be false for the daemon.
    // This is handled by IsDaemonApp() in GhostApp.Core.cs
    if (Connection != null)
    {
      G.LogWarn("Daemon has a GhostFatherConnection initialized. This is usually not intended for the daemon itself.");
      await Connection.StopReporting(); // Stop it if it was started
    }

    await RegisterSelfDirectlyAsync();
    await _processManager.InitializeAsync(); // Discovers and potentially starts managed apps

    _ = _commandProcessor.StartProcessingAsync(CancellationToken.None); // Fire and forget
    _ = _communicationServer.StartAsync(CancellationToken.None); // Fire and forget

    RegisterCommandHandlers();
    await _processManager.DiscoverGhostAppsAsync(); // Initial discovery

    // Daemon is a service, so IsService should be true.
    // The tick timer is handled by the base GhostApp if IsService is true.
    // We use OnTickAsync for periodic daemon tasks.
    if (!IsService)
    {
      G.LogWarn("GhostFatherDaemon is not configured as a service. It might exit prematurely if RunAsync completes.");
    }

    _daemonMetricsReportingTimer = new Timer(ReportDaemonMetricsCallback, null, TimeSpan.FromSeconds(10), Config.Core.MetricsInterval);
    RegisterDisposalAction(async () =>
    {
      if (_daemonMetricsReportingTimer != null) await _daemonMetricsReportingTimer.DisposeAsync();
    });


    G.LogInfo("GhostFatherDaemon initialized and ready. Waiting for commands and events...");
    // For a service, RunAsync might wait on a CancellationToken or a TaskCompletionSource
    // that is completed by StopAsync. The base GhostApp handles the service loop via OnTickAsync.
    // If this RunAsync completes, and IsService is true, the app might not behave as a long-running service
    // unless the base class's tick loop keeps it alive.
    // For now, we'll let it complete and rely on OnTickAsync for periodic work.
  }

  protected override async Task OnBeforeRunAsync()
  {
    G.LogInfo("GhostFatherDaemon preparing to run...");
    // Ensure required directories exist using paths from Config
    Directory.CreateDirectory(Config.Core.LogsPath ?? Path.Combine(Directory.GetCurrentDirectory(), "logs"));
    Directory.CreateDirectory(Config.Core.DataPath ?? Path.Combine(Directory.GetCurrentDirectory(), "data"));
    // Assuming an "apps" path might be needed for managed applications
    Directory.CreateDirectory(Path.Combine(Config.Core.DataPath ?? Directory.GetCurrentDirectory(), "apps"));

    // Base OnBeforeRunAsync is empty, so no need to call it.
  }

  /// <summary>
  /// Periodic tasks for the daemon.
  /// </summary>
  protected override async Task OnTickAsync()
  {
    if (State != GhostAppState.Running) return;

    try
    {
      await _healthMonitor.CheckHealthAsync(); // Monitors connected apps
      await _processManager.MaintenanceTickAsync(); // Manages child processes
      await _communicationServer.CheckConnectionsAsync(); // Checks for timed-out app connections

      // Persist state periodically (e.g., every minute)
      if (DateTime.UtcNow.Second == 0) // Example: once per minute
      {
        await _stateManager.PersistStateAsync();
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Error during GhostFatherDaemon's OnTickAsync.");
      // Consider if an error here should mark the daemon as failed or attempt recovery.
      // For now, log and continue. AutoRestart from base class might trigger if this throws.
    }
  }

    #endregion

    #region Daemon Specific Initialization and Operations

  private async Task InitializeDaemonServicesAsync()
  {
    try
    {
      await _stateManager.InitializeAsync();
      G.LogInfo("Daemon services (StateManager) initialized successfully.");
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to initialize daemon-specific services.");
      throw; // Propagate to main error handling
    }
  }

  private async Task RegisterSelfDirectlyAsync()
  {
    G.LogInfo("Registering daemon process with its own ProcessManager and AppCommunicationServer.");
    try
    {
      var process = System.Diagnostics.Process.GetCurrentProcess();
      var registration = new ProcessRegistration
      {
          Id = _daemonId,
          Name = Config.App.Name ?? "GhostFather Daemon",
          Type = "daemon",
          Version = Config.App.Version ?? GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0",
          ExecutablePath = Environment.ProcessPath ?? process.MainModule?.FileName ?? "unknown",
          Arguments = string.Join(" ", GetCommandLineArgsSkipFirst()),
          WorkingDirectory = Directory.GetCurrentDirectory(),
          Environment = new Dictionary<string, string>
          {
              ["PID"] = process.Id.ToString()
          },
          Configuration = new Dictionary<string, string>
          {
              ["AppType"] = "daemon"
          }
      };

      await _processManager.RegisterProcessAsync(registration);

      var daemonConnectionInfo = new AppConnectionInfo // Defined in GhostFatherDaemon.cs or accessible namespace
      {
          Id = _daemonId,
          Metadata = new ProcessMetadata(registration.Name, registration.Type, registration.Version, registration.Environment, registration.Configuration),
          Status = "Running",
          LastSeen = DateTime.UtcNow,
          IsDaemon = true
      };
      await _communicationServer.RegisterConnectionAsync(daemonConnectionInfo); // Use the correct method name
      G.LogInfo($"Daemon '{_daemonId}' registered locally.");
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to register daemon process with its own services.");
      throw;
    }
  }

  private void RegisterCommandHandlers()
  {
    G.LogInfo("Registering daemon command handlers...");
    _commandProcessor.RegisterHandler("start", HandleStartCommandAsync);
    _commandProcessor.RegisterHandler("stop", HandleStopCommandAsync);
    _commandProcessor.RegisterHandler("restart", HandleRestartCommandAsync);
    _commandProcessor.RegisterHandler("status", HandleStatusCommandAsync);
    _commandProcessor.RegisterHandler("register", HandleRegisterCommandAsync);
    _commandProcessor.RegisterHandler("run", HandleRunCommandAsync);
    _commandProcessor.RegisterHandler("ping", HandlePingCommandAsync);
    _commandProcessor.RegisterHandler("connections", HandleConnectionsCommandAsync);
    _commandProcessor.RegisterHandler("discover", HandleDiscoverCommandAsync);
    // Add more handlers as needed
    G.LogInfo("Daemon command handlers registered.");
  }

    #endregion

    #region Command Handlers (Example: Ping)

  private async Task HandlePingCommandAsync(SystemCommand cmd)
  {
    try
    {
      var responseChannel = cmd.Parameters.GetValueOrDefault("responseChannel", "ghost:responses:unknown");
      G.LogInfo($"Received ping command: {cmd.CommandId}. Will respond on: {responseChannel}");

      var daemonProcess = System.Diagnostics.Process.GetCurrentProcess();
      var pingResponseData = new Dictionary<string, object>
      {
          ["DaemonStatus"] = "Running",
          ["DaemonVersion"] = Config.App.Version,
          ["ManagedProcesses"] = _processManager.GetAllProcesses().Count(),
          ["ConnectedApps"] = _communicationServer.GetActiveConnections().Count(),
          ["DaemonUptimeSeconds"] = (DateTime.UtcNow - daemonProcess.StartTime.ToUniversalTime()).TotalSeconds,
          ["DaemonMemoryUsageMB"] = Math.Round(daemonProcess.WorkingSet64 / (1024.0 * 1024.0), 2)
      };

      var response = new CommandResponse
      {
          CommandId = cmd.CommandId,
          Success = true,
          Data = new StringResponse
          {
              Value = JsonSerializer.Serialize(pingResponseData)
          },
          Timestamp = DateTime.UtcNow
      };
      await Bus.PublishAsync(responseChannel, response);
      G.LogInfo($"Ping response sent for {cmd.CommandId} to {responseChannel}.");
    }
    catch (Exception ex)
    {
      G.LogError(ex, $"Failed to process ping command: {cmd.CommandId}");
      // Optionally try to send an error response
    }
  }

  // Implement other command handlers (HandleStartCommandAsync, HandleStopCommandAsync, etc.)
  // These will use _processManager, _stateManager, etc.
  // Example:
  private async Task HandleStatusCommandAsync(SystemCommand cmd)
  {
    var responseChannel = cmd.Parameters.GetValueOrDefault("responseChannel", "ghost:responses:unknown");
    try
    {
      var processId = cmd.Parameters.GetValueOrDefault("processId");
      object statusData;
      if (!string.IsNullOrEmpty(processId))
      {
        var process = await _processManager.GetProcessAsync(processId);
        statusData = process.GetProcessState() ?? throw new InvalidOperationException();
      } else
      {
        statusData = (await _processManager.GetAllProcessesAsync()).Select(p => p.GetProcessState()).ToList();
      }
      await SendCommandResponseAsync(cmd, true, new StringResponse
      {
          Value = JsonSerializer.Serialize(statusData)
      }, responseChannel);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to handle status command.");
      await SendCommandResponseAsync(cmd, false, null, responseChannel, ex.Message);
    }
  }
  private async Task HandleStartCommandAsync(SystemCommand cmd)
  {
    var responseChannel = cmd.Parameters.GetValueOrDefault("responseChannel", "ghost:responses:unknown");
    string processId = cmd.Parameters.GetValueOrDefault("processId");
    if (string.IsNullOrEmpty(processId))
    {
      await SendCommandResponseAsync(cmd, false, null, responseChannel, "ProcessId is required.");
      return;
    }
    try
    {
      await _processManager.StartProcessAsync(processId);
      var process = await _processManager.GetProcessAsync(processId);
      await SendCommandResponseAsync(cmd, true, new StringResponse
      {
          Value = JsonSerializer.Serialize(process?.GetProcessState())
      }, responseChannel);
    }
    catch (Exception ex)
    {
      G.LogError(ex, $"Failed to start process {processId}.");
      await SendCommandResponseAsync(cmd, false, null, responseChannel, ex.Message);
    }
  }
  // ... other handlers ...
  private async Task HandleStopCommandAsync(SystemCommand cmd)
  { /* ... */
  }
  private async Task HandleRestartCommandAsync(SystemCommand cmd)
  { /* ... */
  }
  private async Task HandleRegisterCommandAsync(SystemCommand cmd)
  { /* ... */
  }
  private async Task HandleRunCommandAsync(SystemCommand cmd)
  { /* ... */
  }
  private async Task HandleConnectionsCommandAsync(SystemCommand cmd)
  { /* ... */
  }
  private async Task HandleDiscoverCommandAsync(SystemCommand cmd)
  { /* ... */
  }


  private async Task SendCommandResponseAsync(SystemCommand originalCommand, bool success, ICommandData data = null, string responseChannel = null, string error = null)
  {
    responseChannel ??= originalCommand.Parameters.GetValueOrDefault("responseChannel", "ghost:responses:unknown");
    var response = new CommandResponse
    {
        CommandId = originalCommand.CommandId,
        Success = success,
        Data = data,
        Error = error,
        Timestamp = DateTime.UtcNow
    };
    await Bus.PublishAsync(responseChannel, response);
  }

    #endregion

    #region Metrics Reporting for Daemon Itself

  private void ReportDaemonMetricsCallback(object state)
  {
    if (State != GhostAppState.Running) return;
    try
    {
      var daemonMetrics = GetDaemonInternalMetrics();
      // The daemon reports its own metrics for its AppCommunicationServer to track
      // and potentially for other monitoring tools that might subscribe directly.
      _communicationServer.UpdateDaemonMetrics(daemonMetrics); // Internal update

      // Optionally, publish to a general metrics channel if other tools might listen
      // _ = Bus.PublishAsync($"ghost:metrics:{_daemonId}", daemonMetrics);
      // G.LogDebug($"Daemon self-metrics reported/published for {_daemonId}.");
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Error in ReportDaemonMetricsCallback.");
    }
  }

  private ProcessMetrics GetDaemonInternalMetrics()
  {
    var process = System.Diagnostics.Process.GetCurrentProcess();
    process.Refresh(); // Refresh stats
    return new ProcessMetrics(
        ProcessId: _daemonId,
        CpuPercentage: 0, // Calculating CPU requires more complex logic (time-based sampling)
        MemoryBytes: process.WorkingSet64,
        ThreadCount: process.Threads.Count,
        Timestamp: DateTime.UtcNow,
        HandleCount: process.HandleCount,
        GcTotalMemory: GC.GetTotalMemory(false),
        Gen0Collections: GC.CollectionCount(0),
        Gen1Collections: GC.CollectionCount(1),
        Gen2Collections: GC.CollectionCount(2)
    );
  }

    #endregion

    #region Disposal

  public override async ValueTask DisposeAsync()
  {
    G.LogInfo("GhostFatherDaemon initiating shutdown sequence...");
    if (_daemonMetricsReportingTimer != null)
    {
      await _daemonMetricsReportingTimer.DisposeAsync();
    }
    if (_communicationServer != null)
    {
      await _communicationServer.StopAsync();
    }
    if (_processManager != null)
    {
      await _processManager.StopAllAsync(); // Gracefully stop managed processes
    }
    if (_stateManager != null)
    {
      await _stateManager.PersistStateAsync(); // Save final state
    }
    await base.DisposeAsync(); // Handles GhostApp level disposal
    G.LogInfo("GhostFatherDaemon disposed.");
  }

    #endregion
}

