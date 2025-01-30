using Ghost.Infrastructure.Database;
using Ghost.Infrastructure.Monitoring;
namespace Ghost.Infrastructure;

public class ProcessManager
{
  private readonly GhostDatabase _db;

  public ProcessManager(GhostDatabase db)
  {
    _db = db;
  }

  public async Task StartProcess(string name, int pid, int port)
  {
    // Register new process
    var process = await _db.RegisterProcess(name, pid, port);

    // Listen for its heartbeats
    _db.SubscribeToEvent<HeartbeatEvent>("heartbeat", async evt =>
    {
      await _db.UpdateProcessStatus(evt.ProcessId, "running");
    });
  }
}

