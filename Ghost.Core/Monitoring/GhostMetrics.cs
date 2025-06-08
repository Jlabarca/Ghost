namespace Ghost.Monitoring;

// Metric reading record for storing metrics
public record MetricValue(string ProcessCpu, double TotalSeconds, Dictionary<string, string> Dictionary, DateTime Timestamp)
{

    public MetricValue() : this(
            string.Empty,
            0,
            new Dictionary<string, string>(),
            DateTime.UtcNow
    )
    {
    }

    public string Name { get; init; }
    public double Value { get; init; }
    public Dictionary<string, string> Tags { get; init; }
    public DateTime Timestamp { get; init; }
}
public interface IMetricsCollector
{
    Task StartAsync(CancellationToken ct = default(CancellationToken));
    Task StopAsync(CancellationToken ct = default(CancellationToken));
    Task TrackMetricAsync(MetricValue metric);
    Task<IEnumerable<MetricValue>> GetMetricsAsync(
            string name,
            DateTime start,
            DateTime end
    );
}
