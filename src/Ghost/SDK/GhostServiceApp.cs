
using Ghost.Core.Config;
using Microsoft.Extensions.Logging;

namespace Ghost.SDK;
/// <summary>
/// Base class for long-running Ghost service apps
/// Think of this as a "daemon" - it runs continuously until stopped
/// </summary>
public abstract class GhostServiceApp : GhostAppBase
{
    private Task _executionTask;
    private readonly TaskCompletionSource _startCompletionSource;
    private readonly TaskCompletionSource _stopCompletionSource;

    private volatile bool _isStarted;
    private volatile bool _isStopping;

    protected GhostServiceApp(GhostOptions options = null) : base(options)
    {
        _startCompletionSource = new TaskCompletionSource();
        _stopCompletionSource = new TaskCompletionSource();
    }

    /// <summary>
    /// Main execution loop for the service. Override this to implement your service's logic.
    /// </summary>
    protected abstract Task ExecuteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts the service
    /// </summary>
    public async Task StartAsync()
    {
        if (_isStarted)
        {
            throw new InvalidOperationException("Service is already started");
        }

        try
        {
            await InitializeAsync();

            _executionTask = ExecuteAsync(CancellationToken);
            _isStarted = true;

            G.Log("Service started successfully", LogLevel.Information);
            _startCompletionSource.SetResult();
        }
        catch (Exception ex)
        {
            G.Log("Failed to start service", LogLevel.Error, ex);
            _startCompletionSource.SetException(ex);
            throw;
        }
    }

    /// <summary>
    /// Stops the service
    /// </summary>
    public virtual async Task StopAsync()
    {
        if (!_isStarted || _isStopping)
        {
            return;
        }

        try
        {
            _isStopping = true;
            G.Log("Stopping service...", LogLevel.Information);

            _cts?.Cancel();

            if (_executionTask != null)
            {
                try
                {
                    await _executionTask;
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation, ignore
                }
            }

            await ShutdownAsync();

            G.Log("Service stopped successfully", LogLevel.Information);
            _stopCompletionSource.SetResult();
        }
        catch (Exception ex)
        {
            G.Log("Error stopping service", LogLevel.Error, ex);
            _stopCompletionSource.SetException(ex);
            throw;
        }
        finally
        {
            _isStarted = false;
            _isStopping = false;
        }
    }

    /// <summary>
    /// Runs a Ghost service of the specified type
    /// </summary>
    public static async Task RunAsync<T>(GhostOptions options = null) where T : GhostServiceApp
    {
        await using var service = (T)Activator.CreateInstance(typeof(T), options);
        var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await service.StartAsync();
            await Task.Delay(-1, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            await service.StopAsync();
        }
    }

    /// <summary>
    /// Waits for the service to start
    /// </summary>
    public Task WaitForStartAsync() => _startCompletionSource.Task;

    /// <summary>
    /// Waits for the service to stop
    /// </summary>
    public Task WaitForStopAsync() => _stopCompletionSource.Task;
}