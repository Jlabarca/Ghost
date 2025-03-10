using Ghost;
using Ghost.Core.Storage;
using Ghost.SDK;
using System.Diagnostics;
/// <summary>
/// Automatically collects and reports standard system metrics
/// to GhostFather without requiring manual app intervention
/// </summary>
public class AutoMonitor : IAutoMonitor
{
    private readonly IGhostBus _bus;
    private readonly string _appId;
    private readonly string _appName;
    private readonly Timer _metricsTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Func<Dictionary<string, double>>> _customMetricsProviders = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private TimeSpan _interval = TimeSpan.FromSeconds(10);
    private bool _isRunning;
    private bool _disposed;

    // CPU usage calculation fields
    private DateTime _lastCpuCheck = DateTime.UtcNow;
    private TimeSpan _lastCpuTotal = TimeSpan.Zero;
    private double _currentCpuPercent = 0;

    /// <summary>
    /// Creates a new AutoMonitor that reports metrics for the specified application
    /// </summary>
    /// <param name="bus">Message bus for sending metrics</param>
    /// <param name="appId">Unique application ID</param>
    /// <param name="appName">Application name</param>
    public AutoMonitor(IGhostBus bus, string appId, string appName)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _appId = appId ?? throw new ArgumentNullException(nameof(appId));
        _appName = appName ?? throw new ArgumentNullException(nameof(appName));

        // Initialize timer but don't start it yet
        _metricsTimer = new Timer(_ => CollectAndSendMetricsAsync().ConfigureAwait(false),
            null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_isRunning || _disposed) return;

            _isRunning = true;

            // Initial CPU measurement
            UpdateCpuUsage();

            // Start the timer
            _metricsTimer.Change(TimeSpan.Zero, _interval);

            G.LogDebug($"AutoMonitor started for {_appName} ({_appId}) with interval {_interval.TotalSeconds}s");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_isRunning) return;

            _isRunning = false;
            await _metricsTimer.DisposeAsync();

            G.LogDebug($"AutoMonitor stopped for {_appName} ({_appId})");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task TrackEventAsync(string eventName, Dictionary<string, string>? properties = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));

        try
        {
            var eventData = new
            {
                Id = _appId,
                AppName = _appName,
                EventName = eventName,
                Properties = properties ?? new Dictionary<string, string>(),
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync("ghost:events", eventData);
        }
        catch (Exception ex)
        {
            G.LogError(ex, $"Failed to track event {eventName}");
        }
    }

    /// <inheritdoc/>
    public void SetCollectionInterval(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentException("Interval must be positive", nameof(interval));

        if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));

        _interval = interval;

        if (_isRunning)
        {
            _metricsTimer.Change(interval, interval);
            G.LogDebug($"AutoMonitor interval updated to {interval.TotalSeconds}s");
        }
    }

    /// <inheritdoc/>
    public void RegisterCustomMetricsProvider(Func<Dictionary<string, double>> provider)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));
        if (provider == null) throw new ArgumentNullException(nameof(provider));

        _customMetricsProviders.Add(provider);
        G.LogDebug("Custom metrics provider registered");
    }

    private async Task CollectAndSendMetricsAsync()
    {
        if (!_isRunning || _disposed) return;

        try
        {
            // Collect standard system metrics
            var metrics = CollectSystemMetrics();

            // Add custom metrics from providers
            foreach (var provider in _customMetricsProviders)
            {
                try
                {
                    var customMetrics = provider();
                    foreach (var (key, value) in customMetrics)
                    {
                        metrics[$"custom.{key}"] = value;
                    }
                }
                catch (Exception ex)
                {
                    G.LogError(ex, "Error collecting custom metrics");
                }
            }

            // Send metrics to GhostFather
            var message = new
            {
                Id = _appId,
                AppName = _appName,
                Metrics = metrics,
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync($"ghost:metrics:{_appId}", message);
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error collecting or sending metrics");
        }
    }

    private Dictionary<string, object> CollectSystemMetrics()
    {
        var process = Process.GetCurrentProcess();

        // Update CPU usage
        UpdateCpuUsage();

        return new Dictionary<string, object>
        {
            ["system.memory.workingSet"] = process.WorkingSet64,
            ["system.memory.privateBytes"] = process.PrivateMemorySize64,
            ["system.memory.virtualBytes"] = process.VirtualMemorySize64,
            ["system.cpu.percentage"] = _currentCpuPercent,
            ["system.process.threads"] = process.Threads.Count,
            ["system.process.handles"] = process.HandleCount,
            ["system.gc.totalMemory"] = GC.GetTotalMemory(false),
            ["system.gc.gen0Collections"] = GC.CollectionCount(0),
            ["system.gc.gen1Collections"] = GC.CollectionCount(1),
            ["system.gc.gen2Collections"] = GC.CollectionCount(2),
            ["system.runtime.uptime"] = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds
        };
    }

    private void UpdateCpuUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var currentCpuTotal = process.TotalProcessorTime;

            if (_lastCpuCheck != DateTime.MinValue)
            {
                var elapsedTime = now - _lastCpuCheck;
                var elapsedCpu = currentCpuTotal - _lastCpuTotal;

                _currentCpuPercent = elapsedCpu.TotalSeconds / (Environment.ProcessorCount * elapsedTime.TotalSeconds) * 100;
                _currentCpuPercent = Math.Round(_currentCpuPercent, 2);
            }

            _lastCpuCheck = now;
            _lastCpuTotal = currentCpuTotal;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error calculating CPU usage");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;

            _disposed = true;
            _isRunning = false;

            await _metricsTimer.DisposeAsync();
            _cts.Cancel();
            _cts.Dispose();
            _lock.Dispose();

            G.LogDebug($"AutoMonitor disposed for {_appName} ({_appId})");
        }
        finally
        {
            _lock.Release();
        }
    }
}