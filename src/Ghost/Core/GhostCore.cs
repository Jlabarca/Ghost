using Ghost.Core.Config;
using Ghost.Core.Health;
using Ghost.Core.Metrics;
using System.Diagnostics;

namespace Ghost.Core;

public interface IGhostCore
{
  string ProcessId { get; }
  IHealthMonitor Health { get; }
  IMetricsCollector Metrics { get; }
  Task StartAsync(CancellationToken ct = default);
  Task StopAsync(CancellationToken ct = default);
}

public class GhostCore : IGhostCore
{
  private readonly Process _process;
  private readonly CancellationTokenSource _cts;
  private readonly GhostConfig _config;

  public string ProcessId { get; }
  public IHealthMonitor Health { get; }
  public IMetricsCollector Metrics { get; }

  public GhostCore(GhostConfig config)
  {
    _config = config;
    _process = Process.GetCurrentProcess();
    ProcessId = _process.Id.ToString();
    _cts = new CancellationTokenSource();

    Health = new HealthMonitor(config.Core.HealthCheckInterval);
    Metrics = new MetricsCollector(config.Core.MetricsInterval);

    G.LogInfo("GhostCore initialized for process {0}", ProcessId);
  }

  public async Task StartAsync(CancellationToken ct = default)
  {
    try
    {
      await Health.StartAsync(_cts.Token);
      await Metrics.StartAsync(_cts.Token);
      G.LogInfo("GhostCore started for process {0}", ProcessId);
    }
    catch (Exception ex)
    {
      G.LogError("Failed to start GhostCore", ex);
      throw;
    }
  }

  public async Task StopAsync(CancellationToken ct = default)
  {
    try
    {
      _cts.Cancel();
      await Health.StopAsync(ct);
      await Metrics.StopAsync(ct);
      G.LogInfo("GhostCore stopped for process {0}", ProcessId);
    }
    catch (Exception ex)
    {
      G.LogError("Error stopping GhostCore", ex);
      throw;
    }
    finally
    {
      _cts.Dispose();
    }
  }
}