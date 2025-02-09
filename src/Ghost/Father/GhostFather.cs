using Ghost.Core.Config;
using Ghost.SDK;

namespace Ghost.Father;

/// <summary>
/// Core process supervisor and orchestration service for Ghost applications
/// </summary>
public class GhostFather : GhostServiceApp
{
    private readonly ProcessManager _processManager;
    private readonly HealthMonitor _healthMonitor;
    private readonly CommandProcessor _commandProcessor;
    private readonly StateManager _stateManager;

    public GhostFather(GhostOptions options = null) : base(options)
    {
        _processManager = new ProcessManager(Bus, Data, Config, new HealthMonitor(Bus), new StateManager(Data));
        _healthMonitor = new HealthMonitor(Bus);
        _commandProcessor = new CommandProcessor(Bus);
        _stateManager = new StateManager(Data);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        G.LogInfo("GhostFather starting...");

        try
        {
            // Initialize components
            await InitializeAsync();

            // Start process manager
            if (_processManager is ProcessManager pm)
            {
                await pm.InitializeAsync();
            }

            // Start command processing
            _ = _commandProcessor.StartProcessingAsync(ct);

            // Subscribe to system events
            await foreach (var evt in Bus.SubscribeAsync<AutoMonitor.SystemEvent>("ghost:events", ct))
            {
                try
                {
                    await HandleSystemEventAsync(evt);
                }
                catch (Exception ex)
                {
                    G.LogError("Error handling system event: {0}", ex, evt.Type);
                }
            }
        }
        catch (OperationCanceledException)
        {
            G.LogInfo("GhostFather shutdown requested");
            throw;
        }
        catch (Exception ex)
        {
            G.LogError("Fatal error in GhostFather", ex);
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Register command handlers
            _commandProcessor.RegisterHandler("start", HandleStartCommandAsync);
            _commandProcessor.RegisterHandler("stop", HandleStopCommandAsync);
            _commandProcessor.RegisterHandler("restart", HandleRestartCommandAsync);
            _commandProcessor.RegisterHandler("status", HandleStatusCommandAsync);

            G.LogInfo("GhostFather initialized");
        }
        catch (Exception ex)
        {
            G.LogError("Failed to initialize GhostFather", ex);
            throw;
        }
    }

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

            await Bus.PublishAsync("ghost:responses", new
            {
                RequestId = cmd.Parameters.GetValueOrDefault("requestId"),
                Status = status,
                Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            G.LogError("Error getting process status", ex);
            await SendCommandResponseAsync(cmd, false, ex.Message);
        }
    }

    private async Task HandleSystemEventAsync(AutoMonitor.SystemEvent evt)
    {
        // ProcessManager now handles all system events internally
        // GhostFather only needs to handle high-level orchestration events
        switch (evt.Type)
        {
            case "ghost.started":
                G.LogInfo("Ghost environment started");
                break;

            case "ghost.stopping":
                G.LogInfo("Ghost environment stopping");
                break;

            case "ghost.config.changed":
                await HandleConfigChangedAsync(evt);
                break;

            case "ghost.error":
                await HandleSystemErrorAsync(evt);
                break;

            default:
                // Forward unknown events to ProcessManager
                break;
        }
    }

    private async Task HandleConfigChangedAsync(AutoMonitor.SystemEvent evt)
    {
        try
        {
            var config = evt.GetData<GhostConfig>();
            // Handle configuration changes...
            G.LogInfo("Configuration updated");
        }
        catch (Exception ex)
        {
            G.LogError("Error handling config change", ex);
        }
    }

    private async Task HandleSystemErrorAsync(AutoMonitor.SystemEvent evt)
    {
        try
        {
            var error = evt.GetData<SystemError>();
            G.LogError("System error: {0} - {1}", error.Code, error.Message);
            // Handle system error...
        }
        catch (Exception ex)
        {
            G.LogError("Error handling system error", ex);
        }
    }

    private async Task SendCommandResponseAsync(SystemCommand cmd, bool success, string error = null)
    {
        try
        {
            var response = new
            {
                RequestId = cmd.Parameters.GetValueOrDefault("requestId"),
                Command = cmd.CommandType,
                Success = success,
                Error = error,
                Timestamp = DateTime.UtcNow
            };

            await Bus.PublishAsync("ghost:responses", response);
        }
        catch (Exception ex)
        {
            G.LogError("Failed to send command response", ex);
        }
    }

    public override async Task StopAsync()
    {
        try
        {
            if (_processManager is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }

            G.LogInfo("GhostFather stopped");
        }
        catch (Exception ex)
        {
            G.LogError("Error during GhostFather shutdown", ex);
        }

        await base.StopAsync();
    }
}

public class SystemError
{
    public string Code { get; set; }
    public string Message { get; set; }
    public Dictionary<string, string> Context { get; set; }
}