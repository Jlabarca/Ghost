using Ghost.Core.Config;
using Ghost.SDK;

namespace Ghost.Father.Daemon;

/// <summary>
/// Core process supervisor and orchestration service for Ghost applications
/// </summary>
public class GhostFatherDaemon : GhostApp
{
    private readonly ProcessManager _processManager;
    private readonly HealthMonitor _healthMonitor;
    private readonly CommandProcessor _commandProcessor;
    private readonly StateManager _stateManager;
    private readonly GhostConfig _config;

    public GhostFatherDaemon(GhostConfig config = null) : base(config)
    {
        // Configure as a service
        _config = config;
        IsService = true;
        TickInterval = TimeSpan.FromSeconds(5);
        AutoRestart = true;
        MaxRestartAttempts = 3;

        // Initialize managers
        _processManager = new ProcessManager(Bus, Data, Config, _healthMonitor, _stateManager);
        _healthMonitor = new HealthMonitor(Bus);
        _commandProcessor = new CommandProcessor(Bus);
        _stateManager = new StateManager(Data);
    }

    public override async Task RunAsync(IEnumerable<string> args)
    {
        G.LogInfo("GhostFather starting...");

        try
        {
            // Initialize components
            await InitializeAsync();

            // Start process manager
            await _processManager.InitializeAsync();

            _ = _commandProcessor.StartProcessingAsync(_cts.Token);

            // Register command handlers
            RegisterCommandHandlers();

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

            //every 5 seconds persist state
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
        Directory.CreateDirectory(_config.GetLogsPath());
        Directory.CreateDirectory(_config.GetDataPath());
        Directory.CreateDirectory(_config.GetAppsPath());

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
    }

    // Command handlers
    private async Task HandleStartCommandAsync(SystemCommand cmd)
    {
        var processId = cmd.Parameters.GetValueOrDefault("processId");
        if (string.IsNullOrEmpty(processId))
        {
            throw new ArgumentException("Process ID required");
        }

        await _processManager.StartProcessAsync(processId);
        await SendCommandResponseAsync(cmd, true);
    }

    private async Task HandleStopCommandAsync(SystemCommand cmd)
    {
        var processId = cmd.Parameters.GetValueOrDefault("processId");
        if (string.IsNullOrEmpty(processId))
        {
            throw new ArgumentException("Process ID required");
        }

        await _processManager.StopProcessAsync(processId);
        await SendCommandResponseAsync(cmd, true);
    }

    private async Task HandleRestartCommandAsync(SystemCommand cmd)
    {
        var processId = cmd.Parameters.GetValueOrDefault("processId");
        if (string.IsNullOrEmpty(processId))
        {
            throw new ArgumentException("Process ID required");
        }

        await _processManager.RestartProcessAsync(processId);
        await SendCommandResponseAsync(cmd, true);
    }

    private async Task HandleStatusCommandAsync(SystemCommand cmd)
    {
        try
        {
            var processId = cmd.Parameters.GetValueOrDefault("processId");
            var status = await _processManager.GetProcessStatusAsync(processId);
            await SendCommandResponseAsync(cmd, true, data: status);
        }
        catch (Exception ex)
        {
            G.LogError("Error getting process status", ex);
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
            await Bus.PublishAsync(responseChannel, response);
        }
        catch (Exception ex)
        {
            G.LogError("Failed to send command response", ex);
        }
    }
}