using MemoryPack;
namespace Ghost;

[MemoryPackable]
public partial class SystemCommand
{
    public string CommandId { get; set; } = "";
    public string CommandType { get; set; } = "";
    public string TargetProcessId { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
    public string Data { get; set; } = ""; // For MemoryPack serialized data (Base64 encoded)
}
