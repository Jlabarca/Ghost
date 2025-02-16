namespace Ghost.Core.Health;

public enum HealthStatus
{
  Unknown,
  Healthy,
  Degraded,
  Unhealthy
}

public record HealthReport(
    HealthStatus Status,
    string Message,
    Dictionary<string, object> Metrics,
    DateTime Timestamp
);

public interface IHealthMonitor
{
  HealthStatus CurrentStatus { get; }
  Task StartAsync(CancellationToken ct = default);
  Task StopAsync(CancellationToken ct = default);
  Task ReportHealthAsync(HealthReport report);
  event EventHandler<HealthReport> HealthChanged;
}
