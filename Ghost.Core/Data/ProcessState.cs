using System.ComponentModel.DataAnnotations;
namespace Ghost.Core.Data;

public class ProcessState
{
  [Key]
  public string ProcessId { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string? Error { get; set; }
  public DateTime Timestamp { get; set; }
}
