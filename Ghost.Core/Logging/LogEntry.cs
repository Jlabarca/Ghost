using Microsoft.Extensions.Logging;
namespace Ghost.Logging;

public class LogEntry
{
  public DateTime Timestamp { get; set; }
  public LogLevel Level { get; set; }
  public string Message { get; set; } = "";
  public string? Exception { get; set; }
  public string ProcessId { get; set; } = "";
  public string? SourceFilePath { get; set; }
  public int SourceLineNumber { get; set; }
}
