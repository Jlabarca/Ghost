using Ghost.Core.Monitoring;
namespace Ghost.Core.Testing.InMemory;

/// <summary>
/// In-memory metrics collector for testing.
/// </summary>
public class InMemoryMetricsCollector : IMetricsCollector
{
  private readonly Dictionary<string, long> _counters = new();
  private readonly Dictionary<string, double> _gauges = new();
  private readonly Dictionary<string, List<long>> _histograms = new();

  /// <summary>
  /// Gets the current value of a counter.
  /// </summary>
  /// <param name="name">The name of the counter.</param>
  /// <returns>The current value of the counter.</returns>
  public long GetCounter(string name)
  {
    return _counters.TryGetValue(name, out var value) ? value : 0;
  }

  /// <summary>
  /// Gets the current value of a gauge.
  /// </summary>
  /// <param name="name">The name of the gauge.</param>
  /// <returns>The current value of the gauge.</returns>
  public double GetGauge(string name)
  {
    return _gauges.TryGetValue(name, out var value) ? value : 0;
  }

  /// <summary>
  /// Gets the recorded values of a histogram.
  /// </summary>
  /// <param name="name">The name of the histogram.</param>
  /// <returns>The recorded values of the histogram.</returns>
  public IReadOnlyList<long> GetHistogram(string name)
  {
    return _histograms.TryGetValue(name, out var values) ? values : Array.Empty<long>();
  }

  /// <inheritdoc />
  public void IncrementCounter(string name, double increment = 1)
  {
    _counters[name] = _counters.TryGetValue(name, out var value) ? value + (long)increment : (long)increment;
  }

  /// <inheritdoc />
  public void RecordGauge(string name, double value)
  {
    _gauges[name] = value;
  }

  /// <inheritdoc />
  public void RecordLatency(string name, long milliseconds)
  {
    if (!_histograms.TryGetValue(name, out var values))
    {
      values = new List<long>();
      _histograms[name] = values;
    }

    values.Add(milliseconds);
  }

  /// <summary>
  /// Clears all recorded metrics.
  /// </summary>
  public void Clear()
  {
    _counters.Clear();
    _gauges.Clear();
    _histograms.Clear();
  }
  public Task StartAsync(CancellationToken ct = default)
  {
    throw new NotImplementedException();
  }
  public Task StopAsync(CancellationToken ct = default)
  {
    throw new NotImplementedException();
  }
  public Task TrackMetricAsync(MetricValue metric)
  {
    throw new NotImplementedException();
  }
  public Task<IEnumerable<MetricValue>> GetMetricsAsync(string name, DateTime start, DateTime end)
  {
    throw new NotImplementedException();
  }
}
