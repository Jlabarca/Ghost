using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Orchestration;
using System.Diagnostics;
using Ghost.Infrastructure.Monitoring;

namespace Ghost.SDK.Monitoring;

/// <summary>
/// Interface for automatic process monitoring
/// Think of this as a "health monitoring system" that keeps track of vital signs
/// </summary>
public interface IAutoMonitor
{
    Task StartAsync(TimeSpan interval);
    Task StopAsync();
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

/// <summary>
/// Implementation of automatic process monitoring
/// This is like having "sensors" throughout the building that continuously monitor various metrics
/// </summary>
public class AutoMonitor : IAutoMonitor
{
    private readonly IRedisManager _redisManager;
    private readonly IDataAPI _dataApi;
    private readonly string _processId;
    private readonly CancellationTokenSource _cts;
    private Task _monitoringTask;
    private readonly Process _currentProcess;

    public event EventHandler<ProcessMetricsEventArgs> MetricsCollected;

    public AutoMonitor(
        IRedisManager redisManager,
        IDataAPI dataApi)
    {
        _redisManager = redisManager;
        _dataApi = dataApi;
        _processId = Guid.NewGuid().ToString();
        _cts = new CancellationTokenSource();
        _currentProcess = Process.GetCurrentProcess();
    }

    public async Task StartAsync(TimeSpan interval)
    {
        if (_monitoringTask != null)
            throw new InvalidOperationException("Monitoring is already started");

        _monitoringTask = MonitorProcessAsync(interval);
    }

    public async Task StopAsync()
    {
        if (_monitoringTask == null)
            return;

        _cts.Cancel();
        await _monitoringTask;
        _monitoringTask = null;
    }

    private async Task MonitorProcessAsync(TimeSpan interval)
    {
        var lastCpuTime = TimeSpan.Zero;
        var lastMeasurement = DateTime.UtcNow;

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Calculate CPU usage since last measurement
                var currentCpuTime = _currentProcess.TotalProcessorTime;
                var cpuUsage = (currentCpuTime - lastCpuTime).TotalMilliseconds /
                              (DateTime.UtcNow - lastMeasurement).TotalMilliseconds /
                              Environment.ProcessorCount * 100;

                lastCpuTime = currentCpuTime;
                lastMeasurement = DateTime.UtcNow;

                // Collect current metrics
                var metrics = new ProcessMetrics(
                    _processId,
                    cpuUsage,
                    _currentProcess.WorkingSet64,
                    _currentProcess.Threads.Count,
                    DateTime.UtcNow
                );

                // Store metrics
                await StoreMetrics(metrics);

                // Raise event
                OnMetricsCollected(metrics);

                // Wait for next interval
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

    private async Task StoreMetrics(ProcessMetrics metrics)
    {
        // Store in Redis for real-time access
        await _redisManager.PublishMetricsAsync(metrics);

        // Store in persistent storage for historical analysis
        var key = $"metrics:{_processId}:history:{DateTime.UtcNow:yyyyMMddHH}";
        await _dataApi.SetDataAsync(key, metrics);
    }

    protected virtual void OnMetricsCollected(ProcessMetrics metrics)
    {
        MetricsCollected?.Invoke(this, new ProcessMetricsEventArgs(
            metrics,
            DateTime.UtcNow
        ));
    }
}

/// <summary>
/// Extension methods for metrics analysis
/// These are like the "analysis tools" that help make sense of the raw sensor data
/// </summary>
public static class MetricsAnalysisExtensions
{
    public static double GetAverageCpuUsage(this IEnumerable<ProcessMetrics> metrics)
        => metrics.Average(m => m.CpuPercentage);

    public static long GetPeakMemoryUsage(this IEnumerable<ProcessMetrics> metrics)
        => metrics.Max(m => m.MemoryBytes);

    public static double GetAverageThreadCount(this IEnumerable<ProcessMetrics> metrics)
        => metrics.Average(m => m.ThreadCount);

    public static ProcessMetricsSummary GetSummary(this IEnumerable<ProcessMetrics> metrics)
    {
        var orderedMetrics = metrics.OrderBy(m => m.Timestamp).ToList();
        return new ProcessMetricsSummary
        {
            StartTime = orderedMetrics.First().Timestamp,
            EndTime = orderedMetrics.Last().Timestamp,
            AverageCpu = GetAverageCpuUsage(orderedMetrics),
            PeakMemory = GetPeakMemoryUsage(orderedMetrics),
            AverageThreads = GetAverageThreadCount(orderedMetrics),
            SampleCount = orderedMetrics.Count
        };
    }
}