using Ghost.Config;
using Ghost.Data;
using Ghost.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace Ghost;

public enum GhostAppState
{
    Created,
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed
}
public abstract partial class GhostApp : IAsyncDisposable
{

#region Tick Timer

    private async void OnTickCallback(object state)
    {
        if (State != GhostAppState.Running || _cts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await OnTickAsync();
            await ReportMetricsAsync(); // Reporting is optional and checks Connection internally, defined in GhostApp.Connection.cs
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error in OnTickAsync execution.");
            OnErrorOccurred(ex); // Notify, but don't necessarily stop the service for a tick error
        }
    }

#endregion

    // Helper to determine if the current app is the daemon itself
    // to avoid connecting to itself.
    private bool IsDaemonApp()
    {
        return Config?.App?.Id?.Equals("ghost-daemon", StringComparison.OrdinalIgnoreCase) == true ||
               GetType().Name.Equals("GhostFatherDaemon", StringComparison.OrdinalIgnoreCase);
    }

#region Properties

    public GhostConfig Config { get; private set; }
    public GhostAppState State { get; private set; } = GhostAppState.Created;
    public IServiceProvider Services { get; private set; }

    // Settings to be applied (defaults can be set by attributes)
    public bool IsService { get; protected set; }
    public bool AutoGhostFather { get; protected set; } = true;
    public bool AutoMonitor { get; protected set; } = true;
    public bool AutoRestart { get; protected set; }
    public int MaxRestartAttempts { get; protected set; }
    public TimeSpan TickInterval { get; protected set; } = TimeSpan.FromSeconds(5);

    // Protected access to core services for derived classes
    protected IGhostBus Bus => _busLazy.Value;
    protected ICache Cache => _cacheLazy.Value;
    protected IGhostData Data => _dataLazy.Value; // Added IGhostData

#endregion

#region Private Fields

    private CancellationTokenSource _cts = new CancellationTokenSource();
    private int _restartAttempts;
    private DateTime? _lastRestartTime;
    private IEnumerable<string> _lastArgs;
    private readonly List<Func<ValueTask>> _disposalActions = new List<Func<ValueTask>>();
    private Timer _tickTimer;

    // Lazy initialization for Bus, Cache, and Data to ensure they are resolved after DI is built
    private Lazy<IGhostBus> _busLazy;
    private Lazy<ICache> _cacheLazy;
    private Lazy<IGhostData> _dataLazy; // Added for IGhostData

#endregion

#region Events

    public event EventHandler<GhostAppState> StateChanged;
    public event EventHandler<Exception> ErrorOccurred;

#endregion

#region Public Execution Entry Point

    public void Execute(IEnumerable<string> args = null, GhostConfig config = null)
    {
        ExecuteAsync(args, config).GetAwaiter().GetResult();
    }

    public async Task ExecuteAsync(IEnumerable<string> args = null, GhostConfig? config = null)
    {
        if (State != GhostAppState.Created && State != GhostAppState.Stopped && State != GhostAppState.Failed)
        {
            G.LogWarn($"Cannot start {GetType().Name} in state {State}.");
            return;
        }

        _lastArgs = args;

        try
        {
            // 1. Load Configuration

            Config = config; // ?? LoadConfigFromYaml() ?? CreateDefaultConfig(); // LoadConfigFromYaml defined in GhostApp.Settings.cs
            G.SetLogLevel(Config.Core.LogLevel.GetValueOrDefault(LogLevel.Information));
            G.LogInfo($"Using App ID: {Config.App.Id}, Name: {Config.App.Name}");
            G.LogDebug(Config.ToYaml() ?? "Config is null, using default configuration.");

            // 2. Configure Services (DI Container Setup)
            Services = ConfigureServicesBase(); // This will call the partial ConfigureServices method, defined in GhostApp.Services.cs
            _busLazy = new Lazy<IGhostBus>(() => Services.GetRequiredService<IGhostBus>());
            _cacheLazy = new Lazy<ICache>(() => Services.GetRequiredService<ICache>());
            _dataLazy = new Lazy<IGhostData>(() => Services.GetRequiredService<IGhostData>());


            // 3. Apply Settings (from attributes and config)
            ApplySettings(); // Defined in GhostApp.Settings.cs

            // 4. Initialize GhostFather Connection (Optional)
            if (AutoGhostFather && !IsDaemonApp()) // Daemons usually don't connect to a "father"
            {
                await InitializeGhostFatherConnectionAsync(); // Defined in GhostApp.Connection.cs
            }
            else
            {
                G.LogInfo("GhostFather connection is disabled for this application or by configuration.");
                // Ensure Connection property is null if not initialized
                Connection = null; // Connection is defined in GhostApp.Connection.cs
            }

            // 5. Core Application Lifecycle
            await OnBeforeRunAsync();
            UpdateState(GhostAppState.Starting);
            G.LogInfo($"Starting {GetType().Name}...");
            await ReportHealthAsync("Starting", "Application is starting."); // ReportHealthAsync defined in GhostApp.Connection.cs

            if (IsService && TickInterval > TimeSpan.Zero)
            {
                _tickTimer = new Timer(OnTickCallback, null, TickInterval, TickInterval);
                RegisterDisposalAction(async () =>
                {
                    if (_tickTimer != null)
                    {
                        await _tickTimer.DisposeAsync();
                    }
                });
            }

            UpdateState(GhostAppState.Running);
            await RunAsync(_lastArgs); // Abstract method for derived classes

            if (!IsService) // One-shot apps
            {
                await ReportHealthAsync("Completed", "Application completed successfully.");
                UpdateState(GhostAppState.Stopped);
            }
            await OnAfterRunAsync();
        }
        catch (Exception ex)
        {
            G.LogError(ex, $"{GetType().Name}.cs");
            UpdateState(GhostAppState.Failed);
            await ReportHealthAsync("Error", $"Application error: {ex.Message}");
            OnErrorOccurred(ex);
            await OnErrorAsync(ex);

            if (AutoRestart && (MaxRestartAttempts == 0 || _restartAttempts < MaxRestartAttempts))
            {
                await HandleAutoRestartAsync(ex);
            }
            else if (IsService) // If a service fails and no restart, it's stopped.
            {
                UpdateState(GhostAppState.Stopped);
            }
            // If not a service and not auto-restarting, it remains Failed.
        }
    }

#endregion

#region Abstract and Virtual Lifecycle Methods

    public abstract Task RunAsync(IEnumerable<string> args);
    protected virtual Task OnBeforeRunAsync()
    {
        return Task.CompletedTask;
    }
    protected virtual Task OnAfterRunAsync()
    {
        return Task.CompletedTask;
    }
    protected virtual Task OnErrorAsync(Exception ex)
    {
        return Task.CompletedTask;
    }
    protected virtual Task OnTickAsync()
    {
        return Task.CompletedTask;
    }

#endregion

#region State and Error Handling

    private void UpdateState(GhostAppState newState)
    {
        if (State == newState)
        {
            return;
        }
        GhostAppState oldState = State;
        State = newState;
        G.LogDebug($"App state changed for {Config?.App?.Id ?? GetType().Name}: {oldState} -> {newState}");
        try
        {
            StateChanged?.Invoke(this, newState);
        }
        catch (Exception ex)
        {
            G.LogWarn($"Error invoking StateChanged event handler: {ex.Message}");
        }
    }

    private void OnErrorOccurred(Exception ex)
    {
        try
        {
            ErrorOccurred?.Invoke(this, ex);
        }
        catch (Exception eventEx)
        {
            G.LogWarn($"Error invoking ErrorOccurred event handler: {eventEx.Message}");
        }
    }

    private async Task HandleAutoRestartAsync(Exception ex)
    {
        _restartAttempts++;
        DateTime now = DateTime.UtcNow;
        if (_lastRestartTime.HasValue && (now - _lastRestartTime.Value).TotalMinutes > 5)
        {
            _restartAttempts = 1; // Reset counter after a period of stability
        }
        _lastRestartTime = now;

        double backoffSeconds = Math.Min(60, Math.Pow(2, _restartAttempts - 1)); // Max 1 min backoff
        double jitter = new Random().NextDouble() * 0.5 + 0.5; // 50-100% of base delay
        int delayMs = (int)(backoffSeconds * 1000 * jitter);

        G.LogInfo($"Application failed. Restarting in {delayMs / 1000.0:0.0}s (Attempt: {_restartAttempts}{(MaxRestartAttempts > 0 ? $"/{MaxRestartAttempts}" : "")})");
        await ReportHealthAsync("Restarting", $"Restarting after error: {ex.Message}");

        await Task.Delay(delayMs, _cts.Token);
        if (!_cts.IsCancellationRequested)
        {
            // Re-execute with the last known arguments and original config
            await ExecuteAsync(_lastArgs, Config);
        }
    }

#endregion

#region Stop and Dispose

    public async Task StopAsync()
    {
        if (State == GhostAppState.Stopped || State == GhostAppState.Stopping)
        {
            return;
        }

        UpdateState(GhostAppState.Stopping);
        G.LogInfo($"Stopping {Config?.App?.Name ?? GetType().Name}...");
        if (_cts != null && !_cts.IsCancellationRequested) // Ensure _cts is not null and not already cancelled
        {
            _cts.Cancel(); // Signal cancellation to ongoing operations
        }


        await ReportHealthAsync("Stopping", "Application is stopping.");
        // Allow time for graceful shutdown of RunAsync if it respects cancellation
        // Forcing RunAsync to stop might require more complex cancellation patterns within its implementation.

        if (_tickTimer != null)
        {
            await _tickTimer.DisposeAsync();
            _tickTimer = null;
        }

        UpdateState(GhostAppState.Stopped);
        G.LogInfo($"{Config?.App?.Name ?? GetType().Name} stopped.");
    }

    protected void RegisterDisposalAction(Func<ValueTask> action)
    {
        _disposalActions.Add(action);
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (State != GhostAppState.Stopped && State != GhostAppState.Failed && State != GhostAppState.Created)
        {
            await StopAsync();
        }

        // Execute custom disposal actions first
        // Reverse order to dispose things in opposite order of registration, if that matters
        for (int i = _disposalActions.Count - 1; i >= 0; i--)
        {
            try
            {
                await _disposalActions[i]();
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Error executing disposal action.");
            }
        }
        _disposalActions.Clear();

        if (Connection != null) // Connection is defined in GhostApp.Connection.cs
        {
            await Connection.DisposeAsync();
            Connection = null;
        }

        _cts?.Dispose(); // _cts could be null if ExecuteAsync was never fully run
        _cts = new CancellationTokenSource(); // Re-create for potential re-execution, though full re-init is safer

        // Dispose the IServiceProvider
        if (Services is IAsyncDisposable asyncDisposableProvider)
        {
            await asyncDisposableProvider.DisposeAsync();
        }
        else if (Services is IDisposable disposableProvider)
        {
            disposableProvider.Dispose();
        }
        Services = null;

        G.LogInfo($"{Config?.App?.Name ?? GetType().Name} disposed.");
        GC.SuppressFinalize(this);
    }

#endregion
}
