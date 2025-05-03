using Ghost.Core;
using MemoryPack;
namespace Ghost;

/// <summary>
/// Serializable message for heartbeats
/// </summary>
[MemoryPackable]
public partial class HeartbeatMessage
{
  public string Id { get; set; }
  public string Status { get; set; }
  public DateTime Timestamp { get; set; }
  public string AppType { get; set; }
}

/// <summary>
/// Serializable message for health status
/// </summary>
[MemoryPackable]
public partial class HealthStatusMessage
{
  public string Id { get; set; }
  public string Status { get; set; }
  public string Message { get; set; }
  public string AppType { get; set; }
  public DateTime Timestamp { get; set; }
}

[MemoryPackable]
[MemoryPackUnion(0, typeof(ProcessStateResponse))]
[MemoryPackUnion(1, typeof(ProcessListResponse))]
[MemoryPackUnion(2, typeof(StringResponse))]
[MemoryPackUnion(3, typeof(BooleanResponse))]
// You can add more data types as needed
public partial interface ICommandData { }

// Specific response data types
[MemoryPackable]
public partial class ProcessStateResponse : ICommandData
{
  public ProcessState? State { get; set; }
}

[MemoryPackable]
public partial class ProcessListResponse : ICommandData
{
  public List<ProcessState>? Processes { get; set; }
}

[MemoryPackable]
public partial class StringResponse : ICommandData
{
  public string? Value { get; set; }
}

[MemoryPackable]
public partial class BooleanResponse : ICommandData
{
  public bool Value { get; set; }
}

[MemoryPackable]
public partial class CommandResponse
{
  public string CommandId { get; set; } = "";
  public bool Success { get; set; }
  public string? Error { get; set; }
  public DateTime Timestamp { get; set; }
  public ICommandData? Data { get; set; }
}