using MemoryPack;
namespace Ghost;

//AKA ProcessInfoSnapshot
[MemoryPackable]
public partial class ProcessState
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsRunning { get; set; } = true;
    public bool IsService { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    public ProcessMetrics? LastMetrics { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public ProcessStatus Status { get; set; }
    public int? RestartCount { get; set; }
}
public enum ProcessStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
    Crashed,
    Warning
}
public class ProcessOutputEventArgs : EventArgs
{

    public ProcessOutputEventArgs(string data)
    {
        Data = data;
        Timestamp = DateTime.UtcNow;
    }
    public string Data { get; }
    public DateTime Timestamp { get; }
}
public class ProcessStatusEventArgs : EventArgs
{

    public ProcessStatusEventArgs(
            string processId,
            ProcessStatus oldStatus,
            ProcessStatus newStatus,
            DateTime timestamp)
    {
        ProcessId = processId;
        OldStatus = oldStatus;
        NewStatus = newStatus;
        Timestamp = timestamp;
    }
    public string ProcessId { get; }
    public ProcessStatus OldStatus { get; }
    public ProcessStatus NewStatus { get; }
    public DateTime Timestamp { get; }
}
