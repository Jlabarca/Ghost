
using Dapper;
using Ghost.Infrastructure.Database;
using System.Collections.Concurrent;
namespace Ghost.Infrastructure.Monitoring;

public class MonitorSystem
{
  private readonly GhostDatabase _db;
  private readonly GhostLogger _logger;
  private readonly ConcurrentDictionary<string, ProcessStatus> _processStatus;
  private readonly CancellationTokenSource _cts;
  private Task _monitoringTask;

  public MonitorSystem(GhostDatabase db, GhostLogger logger)
  {
    _db = db;
    _logger = logger;
    _processStatus = new ConcurrentDictionary<string, ProcessStatus>();
    _cts = new CancellationTokenSource();

    //new thread
    Task.Run(StartAsync);
  }

  public Task StartAsync()
  {
    // Start monitoring loop
    _monitoringTask = Task.Run(async () =>
    {
      while (!_cts.Token.IsCancellationRequested)
      {
        try
        {
          await CheckStaleProcesses();
          await Task.Delay(TimeSpan.FromSeconds(10), _cts.Token);
        }
        catch (OperationCanceledException)
        {
          break;
        }
        catch (Exception ex)
        {
          _logger.Log("monitor", $"Error in monitoring loop: {ex.Message}");
        }
      }
    });

    // Subscribe to heartbeats
    _db.SubscribeToEvent<HeartbeatEvent>("heartbeat", HandleHeartbeat);

    return Task.CompletedTask;
  }

  private async Task HandleHeartbeat(HeartbeatEvent evt)
  {
    _processStatus.AddOrUpdate(
        evt.ProcessId,
        id => CreateInitialStatus(id, evt),
        (id, existing) => UpdateExistingStatus(existing, evt)
    );

    await _db.UpdateProcessStatus(evt.ProcessId, "running");
  }

  private ProcessStatus CreateInitialStatus(string id, HeartbeatEvent evt) =>
      new(
          Id: id,
          Name: "Unknown",
          Status: "running",
          Pid: null,
          Port: null,
          Metrics: evt.Metrics,
          LastHeartbeat: evt.Timestamp,
          Uptime: TimeSpan.Zero,
          RestartCount: 0
      );

  private ProcessStatus UpdateExistingStatus(ProcessStatus existing, HeartbeatEvent evt) =>
      existing with
      {
          Status = "running",
          Metrics = evt.Metrics,
          LastHeartbeat = evt.Timestamp,
          Uptime = evt.Timestamp - existing.LastHeartbeat
      };

  public async Task<IEnumerable<ProcessStatus>> GetAllProcessStatus()
  {
    using var conn = _db.CreateConnection();
    var processes = await conn.QueryAsync<ProcessInfo>(
        $"SELECT * FROM {_db.GetTablePrefix()}processes WHERE status != 'terminated'"
    );

    return processes.Select(proc =>
        _processStatus.TryGetValue(proc.Id, out var status)
            ? status with { Name = proc.Name, Pid = proc.Pid, Port = proc.Port }
            : CreateOfflineStatus(proc));
  }

  private ProcessStatus CreateOfflineStatus(ProcessInfo proc) =>
      new(
          Id: proc.Id,
          Name: proc.Name,
          Status: proc.Status,
          Pid: proc.Pid,
          Port: proc.Port,
          Metrics: null,
          LastHeartbeat: DateTime.MinValue,
          Uptime: TimeSpan.Zero,
          RestartCount: 0
      );

  private async Task CheckStaleProcesses()
  {
    var staleTimeout = TimeSpan.FromSeconds(30);
    var now = DateTime.UtcNow;

    foreach (var (id, status) in _processStatus)
    {
      if (now - status.LastHeartbeat > staleTimeout)
      {
        await _db.UpdateProcessStatus(id, "stale");
        _processStatus.TryUpdate(id, status with { Status = "stale" }, status);
      }
    }
  }

  public async Task StopAsync()
  {
    if (_monitoringTask != null)
    {
      _cts.Cancel();
      await _monitoringTask;
      _cts.Dispose();
    }
  }
}
