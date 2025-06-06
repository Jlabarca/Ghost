using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ghost.Monitoring;

public class MetricsCollector : IMetricsCollector
{
    private readonly TimeSpan _interval;
    private readonly ConcurrentDictionary<string, List<MetricValue>> _metrics;
    private readonly Timer _timer;
    private bool _isRunning;

    public MetricsCollector(TimeSpan interval)
    {
        _interval = interval;
        _metrics = new ConcurrentDictionary<string, List<MetricValue>>();
        _timer = new Timer(CollectMetrics);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return Task.CompletedTask;
        
        _isRunning = true;
        _timer.Change(TimeSpan.Zero, _interval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;
        
        _isRunning = false;
        _timer.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public Task TrackMetricAsync(MetricValue metric)
    {
        _metrics.AddOrUpdate(
            metric.Name,
            new List<MetricValue> { metric },
            (_, list) =>
            {
                list.Add(metric);
                // Keep last 1000 values
                while (list.Count > 1000)
                    list.RemoveAt(0);
                return list;
            });

        return Task.CompletedTask;
    }

    public Task<IEnumerable<MetricValue>> GetMetricsAsync(
        string name, DateTime start, DateTime end)
    {
        if (!_metrics.TryGetValue(name, out var metrics))
            return Task.FromResult(Enumerable.Empty<MetricValue>());

        return Task.FromResult(
            metrics.Where(m => m.Timestamp >= start && m.Timestamp <= end)
        );
    }

    private void CollectMetrics(object state)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var timestamp = DateTime.UtcNow;

            TrackMetricAsync(new MetricValue(
                "process.cpu",
                process.TotalProcessorTime.TotalSeconds,
                new Dictionary<string, string>(),
                timestamp
            )).Wait();

            TrackMetricAsync(new MetricValue(
                "process.memory",
                process.WorkingSet64,
                new Dictionary<string, string>(),
                timestamp
            )).Wait();

            TrackMetricAsync(new MetricValue(
                "process.threads",
                process.Threads.Count,
                new Dictionary<string, string>(),
                timestamp
            )).Wait();
        }
        catch (Exception ex)
        {
            G.LogError("Metrics collection failed", ex);
        }
    }
}