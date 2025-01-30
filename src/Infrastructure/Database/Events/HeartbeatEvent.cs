
using Ghost.Infrastructure.Database;
namespace Ghost.Infrastructure.Monitoring;

// Core heartbeat event that processes send
public record HeartbeatEvent(
    string ProcessId,      // Unique identifier of the sending process
    ProcessMetrics Metrics,// Current process metrics
    DateTime Timestamp     // When the heartbeat was sent
)
{
  // Validate required fields
  public void Validate()
  {
    if (string.IsNullOrEmpty(ProcessId))
      throw new ArgumentException("ProcessId is required");

    if (Metrics == null)
      throw new ArgumentException("Metrics are required");

    if (Timestamp == default)
      throw new ArgumentException("Timestamp must be set");
  }

  // Helper to create a heartbeat with current timestamp
  public static HeartbeatEvent Create(string processId, ProcessMetrics metrics)
    => new(processId, metrics, DateTime.UtcNow);
}

// Extension method for the persistence layer
public static class HeartbeatExtensions
{
  // Strongly-typed event subscription
  public static void SubscribeToHeartbeats(
      this GhostDatabase db,
      Func<HeartbeatEvent, Task> handler)
  {
    db.SubscribeToEvent<HeartbeatEvent>("heartbeat", handler);
  }

  // Strongly-typed event publishing
  public static Task PublishHeartbeat(
      this GhostDatabase db,
      HeartbeatEvent heartbeat)
  {
    heartbeat.Validate();
    return db.PublishEvent("heartbeat", heartbeat);
  }
}