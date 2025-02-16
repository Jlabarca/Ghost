using Ghost.Core;
using Ghost.Core.Config;

namespace Ghost.SDK;

/// <summary>
/// Base class for Ghost apps that supports both one-off tasks and long-running services.
/// </summary>
public abstract class GhostApp : GhostAppBase
{
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private bool _isRunning;
    private bool _hasRun;
    private readonly Timer? _tickTimer;
    private readonly TaskCompletionSource _stopSource = new();

    // Service configuration
    protected TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
    protected bool IsService { get; set; }
    protected bool AutoRestart { get; set; }
    protected int MaxRestartAttempts { get; set; } = 3;
    protected TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(5);

    protected GhostApp(GhostConfig config = null) : base(config)
    {
        G.SetCurrent(this);
        if (IsService)
        {
            _tickTimer = new Timer(OnTickCallback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Main execution method. For one-off apps, this runs once.
    /// For services, this sets up any initial state before ticking begins.
    /// </summary>
    public abstract Task RunAsync(IEnumerable<string> args);

    /// <summary>
    /// Optional service tick method. Override this to implement service behavior.
    /// </summary>
    protected virtual Task OnTickAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called before RunAsync(). Override to add initialization logic.
    /// </summary>
    protected virtual Task OnBeforeRunAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called after RunAsync() or when service stops. Override to add cleanup logic.
    /// </summary>
    protected virtual Task OnAfterRunAsync()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called if an error occurs. Override to add custom error handling.
    /// </summary>
    protected virtual Task OnErrorAsync(Exception ex)
    {
        G.LogError(ex, "Error in {Type}", GetType().Name);
        return Task.CompletedTask;
    }

    private async void OnTickCallback(object? state)
    {
        try
        {
            await OnTickAsync();
        }
        catch (Exception ex)
        {
            await OnErrorAsync(ex);
            if (AutoRestart && MaxRestartAttempts > 0)
            {
                MaxRestartAttempts--;
                G.LogWarn("Restarting service after error ({Attempts} attempts remaining)", MaxRestartAttempts);
                await Task.Delay(RestartDelay);
                await StartServiceAsync();
            }
            else
            {
                G.LogError("Service stopped due to error");
                await StopAsync();
            }
        }
    }

    private async Task StartServiceAsync()
    {
        if (IsService && _tickTimer != null)
        {
            _tickTimer.Change(TimeSpan.Zero, TickInterval);
            G.LogInfo("Service started with {Interval}ms tick interval", TickInterval.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Executes the app with lifecycle management
    /// </summary>
    public async Task<bool> ExecuteAsync(IEnumerable<string> args)
    {
        await _runLock.WaitAsync();
        try
        {
            if (_hasRun && !IsService)
                throw new InvalidOperationException("One-off app can only be run once");

            if (_isRunning)
                throw new InvalidOperationException("App is already running");

            _isRunning = true;
            _hasRun = true;

            await InitializeAsync();
            G.LogInfo("Starting {Type}", GetType().Name);

            try
            {
                await OnBeforeRunAsync();
                await RunAsync(args);

                if (IsService)
                {
                    await StartServiceAsync();
                    await _stopSource.Task; // Wait for stop signal
                }

                await OnAfterRunAsync();
                G.LogInfo("{Type} completed successfully", GetType().Name);
                return true;
            }
            catch (Exception ex)
            {
                await OnErrorAsync(ex);
                throw new GhostException(
                    $"App execution failed: {GetType().Name}",
                    ex,
                    ErrorCode.ProcessError
                );
            }
            finally
            {
                _isRunning = false;
                await ShutdownAsync();
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>
    /// Stops a running service
    /// </summary>
    public async virtual Task StopAsync()
    {
        if (!_isRunning || !IsService) return;

        await _runLock.WaitAsync();
        try
        {
            if (_tickTimer != null)
            {
                await _tickTimer.DisposeAsync();
            }
            _stopSource.TrySetResult();
            G.LogInfo("Service stopped");
        }
        finally
        {
            _runLock.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _runLock.WaitAsync();
        try
        {
            if (_isRunning)
            {
                await StopAsync();
            }
            if (_tickTimer != null)
            {
                await _tickTimer.DisposeAsync();
            }
            await base.DisposeAsync();
        }
        finally
        {
            _runLock.Release();
            _runLock.Dispose();
        }
    }
}