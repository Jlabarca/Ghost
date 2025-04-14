using MemoryPack;
namespace Ghost.Core;

[MemoryPackable]
public partial class SystemCommand
{
  public string CommandId { get; set; } = "";
  public string CommandType { get; set; } = "";
  public string TargetProcessId { get; set; } = "";
  public DateTime Timestamp { get; set; } = DateTime.UtcNow;
  public Dictionary<string, string> Parameters { get; set; } = new();
}
