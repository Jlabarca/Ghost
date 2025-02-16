

// src/Ghost/Core/Metrics/IMetricsCollector.cs
namespace Ghost.Core.Metrics;

public record MetricValue(
    string Name,
    double Value,
    Dictionary<string, string> Tags,
    DateTime Timestamp
);

public interface IMetricsCollector
{
  Task StartAsync(CancellationToken ct = default);
  Task StopAsync(CancellationToken ct = default);
  Task TrackMetricAsync(MetricValue metric);
  Task<IEnumerable<MetricValue>> GetMetricsAsync(
      string name,
      DateTime start,
      DateTime end
  );
}
