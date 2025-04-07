using Ghost.Core.Config;
using Ghost.Core.Exceptions;
using Ghost.Core.Monitoring;
using Ghost.SDK;
using System.Text.Json;

namespace Ghost.Father.Daemon;

public class GhostFatherDaemon : GhostApp
{
  private readonly ProcessManager _processManager;
  private readonly HealthMonitor _healthMonitor;
  private readonly CommandProcessor _commandProcessor;
  private readonly StateManager _stateManager;

  public GhostFatherDaemon(GhostConfig? config = null) : base()
  {
    // Initialize components
    _healthMonitor = new HealthMonitor(Ghost.Bus);
    _commandProcessor = new CommandProcessor(Ghost.Bus);
    _stateManager = new StateManager(Ghost.Data);
    _processManager = new ProcessManager(config, _healthMonitor, Ghost.Bus, Ghost.Data, _stateManager);


    // Configure the daemon
    //ConfigureDaemon();

    // Configure as a service
    IsService = true;
    AutoGhostFather = false; // Don't auto-connect to avoid circular connection
  }

  public override async Task RunAsync(IEnumerable<string> args)
  {
    G.LogInfo("GhostFather starting...");

    try
    {
      // Initialize components
      //await InitializeAsync();

      // Register itself for monitoring
      //await _processManager.RegisterSelfAsync();

      // Start process manager
      await _processManager.InitializeAsync();

      // Start command processor
      _ = _commandProcessor.StartProcessingAsync(CancellationToken.None);

      // Register command handlers
      RegisterCommandHandlers();

      // Discover Ghost apps
      //await _processManager.DiscoverGhostAppsAsync();

      G.LogInfo("GhostFather initialized and ready");
    }
    catch (Exception ex)
    {
      G.LogError("Failed to initialize GhostFather", ex);
      throw;
    }
  }

  protected override async Task OnTickAsync()
  {
    try
    {
      // Process periodic tasks
      await _healthMonitor.CheckHealthAsync();
      await _processManager.MaintenanceTickAsync();

      // Periodically persist state
      if (DateTime.Now.Second % 5 == 0)
        await _stateManager.PersistStateAsync();
    }
    catch (Exception ex)
    {
      G.LogError("Error in GhostFather tick", ex);
      // Let base class handle restart if needed
      throw;
    }
  }

  protected override async Task OnBeforeRunAsync()
  {
    G.LogInfo("GhostFather preparing to start...");

    // Ensure required directories exist
    Directory.CreateDirectory(Config.GetLogsPath());
    Directory.CreateDirectory(Config.GetDataPath());
    Directory.CreateDirectory(Config.GetAppsPath());

    await base.OnBeforeRunAsync();
  }

  protected override async Task OnAfterRunAsync()
  {
    try
    {
      G.LogInfo("GhostFather shutting down...");

      // Stop all processes
      await _processManager.StopAllAsync();

      // Persist final state
      await _stateManager.PersistStateAsync();
    }
    catch (Exception ex)
    {
      G.LogError("Error during GhostFather shutdown", ex);
    }
    finally
    {
      await base.OnAfterRunAsync();
    }
  }

  private void RegisterCommandHandlers()
  {
    _commandProcessor.RegisterHandler("start", HandleStartCommandAsync);
    _commandProcessor.RegisterHandler("stop", HandleStopCommandAsync);
    _commandProcessor.RegisterHandler("restart", HandleRestartCommandAsync);
    _commandProcessor.RegisterHandler("status", HandleStatusCommandAsync);
    _commandProcessor.RegisterHandler("register", HandleRegisterCommandAsync);
    _commandProcessor.RegisterHandler("run", HandleRunCommandAsync);
    _commandProcessor.RegisterHandler("ping", HandlePingCommandAsync);
  }

  // Command handlers

  private async Task HandleRegisterCommandAsync(SystemCommand cmd)
  {
    try
    {
      if (!cmd.Parameters.TryGetValue("registration", out var registrationJson))
      {
        throw new ArgumentException("Registration data is required");
      }

      var registration = JsonSerializer.Deserialize<ProcessRegistration>(registrationJson);
      var force = cmd.Parameters.TryGetValue("force", out var forceStr) &&
                  bool.TryParse(forceStr, out var forceBool) && forceBool;

      G.LogInfo($"Registering process: {registration.Name} ({registration.Id})");

      // Check if process already exists
      try
      {
        var existingProcess = await _processManager.GetProcessAsync(registration.Id);

        if (existingProcess != null && !force)
        {
          throw new GhostException($"Process {registration.Id} already exists", ErrorCode.ProcessError);
        } else if (existingProcess != null)
        {
          // Stop existing process if it's running
          if (existingProcess.Status == ProcessStatus.Running)
          {
            await _processManager.StopProcessAsync(existingProcess.Id);
          }
        }
      }
      catch (GhostException ex) when (ex.Code == ErrorCode.ProcessError)
      {
        // Process not found, continue with registration
      }

      // Register process
      await _processManager.RegisterProcessAsync(registration);

      // Send success response
      await SendCommandResponseAsync(cmd, true);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to register process");
      await SendCommandResponseAsync(cmd, false, ex.Message);
    }
  }

  private async Task HandlePingCommandAsync(SystemCommand cmd)
  {
    await SendCommandResponseAsync(cmd, true, data: new
    {
        Status = "Running",
        Timestamp = DateTime.UtcNow,
        Version = Config.App?.Version ?? "1.0.0"
    });
  }

  private async Task HandleStartCommandAsync(SystemCommand cmd)
  {
    try
    {
      if (!cmd.Parameters.TryGetValue("processId", out var processId))
      {
        throw new ArgumentException("Process ID is required");
      }

      G.LogInfo($"Starting process: {processId}");

      // Start the process
      await _processManager.StartProcessAsync(processId);

      // Send success response
      await SendCommandResponseAsync(cmd, true);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to start process");
      await SendCommandResponseAsync(cmd, false, ex.Message);
    }
  }

  private async Task HandleStopCommandAsync(SystemCommand cmd)
  {
    try
    {
      if (!cmd.Parameters.TryGetValue("processId", out var processId))
      {
        throw new ArgumentException("Process ID is required");
      }

      G.LogInfo($"Stopping process: {processId}");

      // Stop the process
      await _processManager.StopProcessAsync(processId);

      // Send success response
      await SendCommandResponseAsync(cmd, true);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to stop process");
      await SendCommandResponseAsync(cmd, false, ex.Message);
    }
  }

  private async Task HandleRestartCommandAsync(SystemCommand cmd)
  {
    try
    {
      if (!cmd.Parameters.TryGetValue("processId", out var processId))
      {
        throw new ArgumentException("Process ID is required");
      }

      G.LogInfo($"Restarting process: {processId}");

      // Restart the process (stop then start)
      await _processManager.StopProcessAsync(processId);
      await _processManager.StartProcessAsync(processId);

      // Send success response
      await SendCommandResponseAsync(cmd, true);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to restart process");
      await SendCommandResponseAsync(cmd, false, ex.Message);
    }
  }

  private async Task HandleStatusCommandAsync(SystemCommand cmd)
  {
    try
    {
      G.LogInfo("Getting processes status");

      // Check if a specific process ID was provided
      if (cmd.Parameters.TryGetValue("processId", out var processId))
      {
        // Get status of a specific process
        var process = await _processManager.GetProcessAsync(processId);

        if (process == null)
        {
          throw new GhostException($"Process {processId} not found", ErrorCode.ProcessError);
        }

        // Send response with single process status
        await SendCommandResponseAsync(cmd, true, data: process);
      } else
      {
        // Get status of all processes
        var processes = await _processManager.GetAllProcessesAsync();

        // Send response with all processes status
        await SendCommandResponseAsync(cmd, true, data: processes);
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to get process status");
      await SendCommandResponseAsync(cmd, false, ex.Message);
    }
  }

  private async Task HandleRunCommandAsync(SystemCommand cmd)
  {
    try
    {
      if (!cmd.Parameters.TryGetValue("command", out var command))
      {
        throw new ArgumentException("Command to run is required");
      }

      string workingDirectory = cmd.Parameters.GetValueOrDefault("workingDir", Directory.GetCurrentDirectory());

      G.LogInfo($"Running command: {command} in {workingDirectory}");

      // Optional arguments
      string args = cmd.Parameters.GetValueOrDefault("args", string.Empty);
      bool waitForExit = cmd.Parameters.TryGetValue("waitForExit", out var waitStr) &&
                         bool.TryParse(waitStr, out var waitBool) && waitBool;

      // Run the command
      var result = await _processManager.RunCommandAsync(command, args, workingDirectory, waitForExit);

      // Send success response with result
      await SendCommandResponseAsync(cmd, true, data: result);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to run command");
      await SendCommandResponseAsync(cmd, false, ex.Message);
    }
  }

  private async Task SendCommandResponseAsync(SystemCommand cmd, bool success, string error = null, object data = null)
  {
    try
    {
      var response = new CommandResponse
      {
          CommandId = cmd.CommandId,
          Success = success,
          Error = error,
          Data = data,
          Timestamp = DateTime.UtcNow
      };

      var responseChannel = cmd.Parameters.GetValueOrDefault("responseChannel", "ghost:responses");
      await Ghost.Bus.PublishAsync(responseChannel, response);
    }
    catch (Exception ex)
    {
      G.LogError("Failed to send command response", ex);
    }
  }
}