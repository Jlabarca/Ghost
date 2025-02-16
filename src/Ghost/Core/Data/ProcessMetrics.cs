using System.ComponentModel.DataAnnotations;
namespace Ghost.Core.Data;

public class ProcessMetrics
{
  [Key]
  public long Id { get; set; }
  [Required]
  public string ProcessId { get; set; } = string.Empty;
  public double CpuPercentage { get; set; }
  public long MemoryBytes { get; set; }
  public int ThreadCount { get; set; }
  public int HandleCount { get; set; }
  public DateTime Timestamp { get; set; }
}
