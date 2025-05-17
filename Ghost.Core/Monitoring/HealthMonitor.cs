using System.Diagnostics;
namespace Ghost.Core.Monitoring;

public class HealthMonitor : IHealthMonitor
{
    private readonly TimeSpan _checkInterval;
    private readonly Timer _timer;
    private HealthStatus _currentStatus = HealthStatus.Unknown;
    private bool _isRunning;

    public HealthStatus CurrentStatus => _currentStatus;
    public event EventHandler<HealthReport> HealthChanged;

    public HealthMonitor(TimeSpan checkInterval)
    {
        _checkInterval = checkInterval;
        _timer = new Timer(CheckHealth);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_isRunning) return Task.CompletedTask;
        
        _isRunning = true;
        _timer.Change(TimeSpan.Zero, _checkInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!_isRunning) return Task.CompletedTask;
        
        _isRunning = false;
        _timer.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public Task ReportHealthAsync(HealthReport report)
    {
        if (_currentStatus != report.Status)
        {
            _currentStatus = report.Status;
            OnHealthChanged(report);
        }
        return Task.CompletedTask;
    }

    private void CheckHealth(object state)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var report = new HealthReport(
                Status: HealthStatus.Healthy,
                Message: "Process is running",
                Metrics: new Dictionary<string, object>
                {
                    ["cpu_time"] = process.TotalProcessorTime.TotalSeconds,
                    ["working_set"] = process.WorkingSet64,
                    ["threads"] = process.Threads.Count
                },
                Timestamp: DateTime.UtcNow
            );

            ReportHealthAsync(report).Wait();
        }
        catch (Exception ex)
        {
            G.LogError("Health check failed", ex);
        }
    }

    private void OnHealthChanged(HealthReport report)
    {
        try
        {
            HealthChanged?.Invoke(this, report);
        }
        catch (Exception ex)
        {
            G.LogError("Error in health change handler", ex);
        }
    }
}