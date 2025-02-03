using Ghost.Father.Models;
using Ghost2.Infrastructure.Monitoring;
using Ghost2.Infrastructure.ProcessManagement;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
namespace Ghost.Father;

/// <summary>
/// Monitors process health and resource usage
/// </summary>
public class HealthMonitor
{
  private readonly IGhostBus _bus;
  private readonly ILogger _logger;
  private readonly ConcurrentDictionary<string, ProcessHealth> _healthCache;
  private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
  private readonly Timer _healthCheckTimer;

  public HealthMonitor(IGhostBus bus, ILogger logger)
  {
    _bus = bus;
    _logger = logger;
    _healthCache = new ConcurrentDictionary<string, ProcessHealth>();
    _healthCheckTimer = new Timer(OnHealthCheck);
  }

  public Task StartMonitoringAsync(CancellationToken ct)
  {
    _healthCheckTimer.Change(TimeSpan.Zero, _checkInterval);
    return Task.CompletedTask;
  }

  public Task RegisterProcessAsync(ProcessInfo process)
  {
    // Initialize health entry
    _healthCache[process.Id] = new ProcessHealth
    {
        ProcessId = process.Id,
        Metrics = ProcessMetrics.CreateSnapshot(process.Id),
        Status = new Dictionary<string, string>
        {
            ["status"] = process.Status.ToString()
        },
        Timestamp = DateTime.UtcNow
    };

    return Task.CompletedTask;
  }

  private async void OnHealthCheck(object state)
  {
    try
    {
      foreach (var (processId, health) in _healthCache)
      {
        // Check if process is still active
        var isActive = await IsProcessActiveAsync(processId);
        if (!isActive)
        {
          _healthCache.TryRemove(processId, out _);
          continue;
        }

        // Request health check
        await _bus.PublishAsync(
            $"ghost:health:{processId}",
            new { timestamp = DateTime.UtcNow });
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error during health check");
    }
  }

  private async Task<bool> IsProcessActiveAsync(string processId)
  {
    try
    {
      // Ping process
      await _bus.PublishAsync(
          $"ghost:ping:{processId}",
          new { timestamp = DateTime.UtcNow });

      // Wait for response
      using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
      await foreach (var _ in _bus.SubscribeAsync<string>(
          $"ghost:pong:{processId}",
          cts.Token))
      {
        return true;
      }

      return false;
    }
    catch
    {
      return false;
    }
  }
}
