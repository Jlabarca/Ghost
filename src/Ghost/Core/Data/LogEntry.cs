using System.ComponentModel.DataAnnotations;
namespace Ghost.Core.Data;

public class LogEntry
{
  [Key]
  public long Id { get; set; }

  [Required]
  public string ProcessId { get; set; } = string.Empty;

  [Required]
  public string Level { get; set; } = string.Empty;

  [Required]
  public string Message { get; set; } = string.Empty;

  public string? Exception { get; set; }

  public string? SourceFile { get; set; }

  public int? SourceLine { get; set; }

  public DateTime Timestamp { get; set; }
}
