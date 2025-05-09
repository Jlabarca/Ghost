using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ghost.Core.Monitoring;

namespace Ghost.Core.Testing.InMemory;

/// <summary>
/// In-memory metrics collector for testing.
/// </summary>
public class InMemoryMetricsCollector : IMetricsCollector
{
    // Store metrics in a list to preserve history
    private readonly List<MetricValue> _metrics = new();

    // For quick lookups by name
    private readonly Dictionary<string, List<MetricValue>> _metricsByName = new();

    // Lock object for thread safety
    private readonly object _lock = new();

    // Tracking collector state
    private bool _isRunning;

    /// <summary>
    /// Starts the metrics collector.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task StartAsync(CancellationToken ct = default)
    {
        _isRunning = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the metrics collector.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task StopAsync(CancellationToken ct = default)
    {
        _isRunning = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tracks a metric.
    /// </summary>
    /// <param name="metric">The metric to track.</param>
    public Task TrackMetricAsync(MetricValue metric)
    {
        if (metric == null)
            throw new ArgumentNullException(nameof(metric));

        if (string.IsNullOrEmpty(metric.Name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metric));

        lock (_lock)
        {
            _metrics.Add(metric);

            if (!_metricsByName.TryGetValue(metric.Name, out var values))
            {
                values = new List<MetricValue>();
                _metricsByName[metric.Name] = values;
            }

            values.Add(metric);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets metrics by name within a time range.
    /// </summary>
    /// <param name="name">The name of the metrics to get.</param>
    /// <param name="start">The start time of the range.</param>
    /// <param name="end">The end time of the range.</param>
    /// <returns>The metrics that match the criteria.</returns>
    public Task<IEnumerable<MetricValue>> GetMetricsAsync(string name, DateTime start, DateTime end)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        lock (_lock)
        {
            if (!_metricsByName.TryGetValue(name, out var values))
                return Task.FromResult<IEnumerable<MetricValue>>(Array.Empty<MetricValue>());

            var result = values
                .Where(m => m.Timestamp >= start && m.Timestamp <= end)
                .ToList();

            return Task.FromResult<IEnumerable<MetricValue>>(result);
        }
    }

    /// <summary>
    /// Gets the current value of a counter.
    /// </summary>
    /// <param name="name">The name of the counter.</param>
    /// <returns>The current value of the counter.</returns>
    public double GetCounter(string name)
    {
        lock (_lock)
        {
            if (!_metricsByName.TryGetValue(name, out var values))
                return 0;

            return values
                .Where(m => m.Tags.TryGetValue("type", out var type) && type == "counter")
                .Sum(m => m.Value);
        }
    }

    /// <summary>
    /// Gets the current value of a gauge.
    /// </summary>
    /// <param name="name">The name of the gauge.</param>
    /// <returns>The current value of the gauge.</returns>
    public double GetGauge(string name)
    {
        lock (_lock)
        {
            if (!_metricsByName.TryGetValue(name, out var values))
                return 0;

            // For gauges, return the most recent value
            var latestGauge = values
                .Where(m => m.Tags.TryGetValue("type", out var type) && type == "gauge")
                .OrderByDescending(m => m.Timestamp)
                .FirstOrDefault();

            return latestGauge?.Value ?? 0;
        }
    }

    /// <summary>
    /// Gets the recorded values of a histogram.
    /// </summary>
    /// <param name="name">The name of the histogram.</param>
    /// <returns>The recorded values of the histogram.</returns>
    public IReadOnlyList<double> GetHistogram(string name)
    {
        lock (_lock)
        {
            if (!_metricsByName.TryGetValue(name, out var values))
                return Array.Empty<double>();

            return values
                .Where(m => m.Tags.TryGetValue("type", out var type) && type == "histogram")
                .Select(m => m.Value)
                .ToList();
        }
    }

    /// <summary>
    /// Increments a counter.
    /// </summary>
    /// <param name="name">The name of the counter.</param>
    /// <param name="increment">The amount to increment the counter by.</param>
    public async Task IncrementCounterAsync(string name, double increment = 1)
    {
        await TrackMetricAsync(new MetricValue
        {
            Name = name,
            Value = increment,
            Tags = new Dictionary<string, string> { ["type"] = "counter" },
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Records a gauge value.
    /// </summary>
    /// <param name="name">The name of the gauge.</param>
    /// <param name="value">The value to record.</param>
    public async Task RecordGaugeAsync(string name, double value)
    {
        await TrackMetricAsync(new MetricValue
        {
            Name = name,
            Value = value,
            Tags = new Dictionary<string, string> { ["type"] = "gauge" },
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Records a latency value.
    /// </summary>
    /// <param name="name">The name of the latency metric.</param>
    /// <param name="milliseconds">The latency in milliseconds.</param>
    public async Task RecordLatencyAsync(string name, long milliseconds)
    {
        await TrackMetricAsync(new MetricValue
        {
            Name = name,
            Value = milliseconds,
            Tags = new Dictionary<string, string> { ["type"] = "histogram" },
            Timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Clears all recorded metrics.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _metrics.Clear();
            _metricsByName.Clear();
        }
    }

    /// <summary>
    /// Gets the total number of metrics recorded.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _metrics.Count;
            }
        }
    }

    /// <summary>
    /// Gets all metrics.
    /// </summary>
    /// <returns>All metrics.</returns>
    public IReadOnlyList<MetricValue> GetAllMetrics()
    {
        lock (_lock)
        {
            return _metrics.ToList();
        }
    }

    /// <summary>
    /// Gets all metrics with a specific name.
    /// </summary>
    /// <param name="name">The name of the metrics to get.</param>
    /// <returns>All metrics with the specified name.</returns>
    public IReadOnlyList<MetricValue> GetMetricsByName(string name)
    {
        lock (_lock)
        {
            if (!_metricsByName.TryGetValue(name, out var values))
                return Array.Empty<MetricValue>();

            return values.ToList();
        }
    }
}