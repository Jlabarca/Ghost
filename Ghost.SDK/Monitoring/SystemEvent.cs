using MemoryPack;
namespace Ghost;

/// <summary>
///     Represents a system event that can be published through the message bus
///     for inter-process communication within the Ghost framework
/// </summary>
[MemoryPackable]
public partial class SystemEvent
{
    public string Type { get; set; }
    public string ProcessId { get; set; }
    public byte[] Data { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>
    ///     Deserializes the Data byte array into an object of type T
    /// </summary>
    public T GetData<T>() where T : class
    {
        if (Data == null || Data.Length == 0)
        {
            return null;
        }
        return MemoryPackSerializer.Deserialize<T>(Data);
    }

    /// <summary>
    ///     Creates a registration event for a process
    /// </summary>
    public static SystemEvent CreateRegistration(string processId, ProcessRegistration registration)
    {
        return new SystemEvent
        {
                Type = "process.registered",
                ProcessId = processId,
                Data = MemoryPackSerializer.Serialize(registration),
                Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    ///     Creates a stopped event for a process
    /// </summary>
    public static SystemEvent CreateStopped(string processId)
    {
        return new SystemEvent
        {
                Type = "process.stopped",
                ProcessId = processId,
                Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    ///     Get a log-friendly representation of the event
    /// </summary>
    public override string ToString()
    {
        return $"{Type} ({ProcessId}) @ {Timestamp:yyyy-MM-dd HH:mm:ss.fff}";
    }

    /// <summary>
    ///     Common event type constants
    /// </summary>
    public static class EventTypes
    {
        public const string ProcessRegistered = "process.registered";
        public const string ProcessStarted = "process.started";
        public const string ProcessStopped = "process.stopped";
        public const string ProcessCrashed = "process.crashed";
        public const string ProcessRestarted = "process.restarted";
        public const string ProcessResourcesExceeded = "process.resources.exceeded";
        public const string HealthStatusChanged = "health.status.changed";
        public const string DaemonStarted = "daemon.started";
        public const string DaemonStopping = "daemon.stopping";
        public const string ConfigChanged = "config.changed";
    }
}
