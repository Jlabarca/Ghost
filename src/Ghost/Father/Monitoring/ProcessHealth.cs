using Ghost.Core.Monitoring;

namespace Ghost.Father.Models;

public class ProcessHealth
{
  public string ProcessId { get; set; }
  public ProcessMetrics Metrics { get; set; }
  public Dictionary<string, string> Status { get; set; }
  public DateTime Timestamp { get; set; }
}
