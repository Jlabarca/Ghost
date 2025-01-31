using System.Collections.Concurrent;
using System.Diagnostics;
using Ghost.Infrastructure.Monitoring;
using Ghost.Infrastructure.Orchestration;

namespace Ghost.SDK.Monitoring;

public class AutoMonitor : IAutoMonitor
{
    private readonly Process _currentProcess;
    private readonly CancellationTokenSource _cts;
    private readonly string _processId;
    private readonly IRedisManager _redisManager;
    private readonly TimeSpan _defaultInterval = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, ProcessMetrics> _lastMetrics = new();

    private Task _monitoringTask;
    private DateTime _lastMeasurement;
    private TimeSpan _lastProcessorTime;
    private long _lastNetworkIn;
    private long _lastNetworkOut;
    private long _lastDiskRead;
    private long _lastDiskWrite;

    public event EventHandler<ProcessMetricsEventArgs> MetricsCollected;

    public AutoMonitor(IRedisManager redisManager, string processId = null)
    {
        _redisManager = redisManager;
        _currentProcess = Process.GetCurrentProcess();
        _processId = processId ?? _currentProcess.Id.ToString();
        _cts = new CancellationTokenSource();

        // Initialize measurement baselines
        _lastMeasurement = DateTime.UtcNow;
        _lastProcessorTime = _currentProcess.TotalProcessorTime;
        _lastNetworkIn = 0;
        _lastNetworkOut = 0;
        // _lastDiskRead = _currentProcess.ReadOperationCount;
        // _lastDiskWrite = _currentProcess.WriteOperationCount;
    }

    public async Task StartAsync(TimeSpan? interval = null)
    {
        if (_monitoringTask != null)
        {
            throw new InvalidOperationException("Monitoring is already started");
        }

        _monitoringTask = MonitorProcessAsync(interval ?? _defaultInterval);
    }

    public async Task StopAsync()
    {
        if (_monitoringTask == null) return;

        _cts.Cancel();
        await _monitoringTask;
        _monitoringTask = null;
    }

    public async Task<ProcessMetrics> GetCurrentMetricsAsync()
    {
        return await Task.Run(() => CollectMetrics());
    }

    private async Task MonitorProcessAsync(TimeSpan interval)
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var metrics = CollectMetrics();
                await PublishMetrics(metrics);
                OnMetricsCollected(metrics);

                await Task.Delay(interval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log error but continue monitoring
                Console.Error.WriteLine($"Error collecting metrics: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);
            }
        }
    }

    private ProcessMetrics CollectMetrics()
    {
        var now = DateTime.UtcNow;
        var currentProcessorTime = _currentProcess.TotalProcessorTime;
        var currentDiskRead = 0;//_currentProcess.ReadOperationCount;
        var currentDiskWrite = 0;//_currentProcess.WriteOperationCount;

        // Calculate rates
        var timeElapsed = (now - _lastMeasurement).TotalSeconds;
        var cpuTimeElapsed = (currentProcessorTime - _lastProcessorTime).TotalMilliseconds;

        var cpuUsage = (cpuTimeElapsed / (timeElapsed * 1000 * Environment.ProcessorCount)) * 100;
        var diskReadRate = (currentDiskRead - _lastDiskRead) / timeElapsed;
        var diskWriteRate = (currentDiskWrite - _lastDiskWrite) / timeElapsed;

        // Update last measurements
        _lastMeasurement = now;
        _lastProcessorTime = currentProcessorTime;
        _lastDiskRead = currentDiskRead;
        _lastDiskWrite = currentDiskWrite;

        var metrics = new ProcessMetrics(
            processId: _processId,
            cpuPercentage: Math.Round(cpuUsage, 2),
            memoryBytes: _currentProcess.WorkingSet64,
            threadCount: _currentProcess.Threads.Count,
            timestamp: now,
            diskReadBytes: (long)diskReadRate,
            diskWriteBytes: (long)diskWriteRate,
            handleCount: _currentProcess.HandleCount,
            gcTotalMemory: GC.GetTotalMemory(false),
            gen0Collections: GC.CollectionCount(0),
            gen1Collections: GC.CollectionCount(1),
            gen2Collections: GC.CollectionCount(2)
        );

        _lastMetrics.AddOrUpdate(_processId, metrics, (_, _) => metrics);
        return metrics;
    }

    private async Task PublishMetrics(ProcessMetrics metrics)
    {
        await _redisManager.PublishMetricsAsync(metrics);
    }

    protected virtual void OnMetricsCollected(ProcessMetrics metrics)
    {
        MetricsCollected?.Invoke(this, new ProcessMetricsEventArgs(metrics, DateTime.UtcNow));
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        _lastMetrics.Clear();
    }
}

public interface IAutoMonitor : IAsyncDisposable
{
    Task StartAsync(TimeSpan? interval = null);
    Task StopAsync();
    Task<ProcessMetrics> GetCurrentMetricsAsync();
    event EventHandler<ProcessMetricsEventArgs> MetricsCollected;
}

public class ProcessMetricsEventArgs : EventArgs
{
    public ProcessMetrics Metrics { get; }
    public DateTime Timestamp { get; }

    public ProcessMetricsEventArgs(ProcessMetrics metrics, DateTime timestamp)
    {
        Metrics = metrics;
        Timestamp = timestamp;
    }
}
