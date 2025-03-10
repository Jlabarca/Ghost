namespace Ghost.Core.Monitoring;

// Metric reading record for storing metrics
public record MetricValue(string ProcessCpu, double TotalSeconds, Dictionary<string, string> Dictionary, DateTime Timestamp)
{

  public string Name { get; init; }
  public double Value { get; init; }
  public Dictionary<string, string> Tags { get; init; }
  public DateTime Timestamp { get; init; }

  public MetricValue(MetricValue processCpu)
  {
    ProcessCpu = processCpu.ProcessCpu;
    TotalSeconds = processCpu.TotalSeconds;
    Dictionary = processCpu.Dictionary;
    Timestamp = processCpu.Timestamp;
  }
}

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
