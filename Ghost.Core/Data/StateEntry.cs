using System.ComponentModel.DataAnnotations;
namespace Ghost.Core.Data;

public class StateEntry
{
  [Key]
  public long Id { get; set; }

  [Required]
  public string Key { get; set; } = string.Empty;

  public string? Value { get; set; }

  [Required]
  public string ChangeType { get; set; } = string.Empty;

  public DateTime Timestamp { get; set; }
}
