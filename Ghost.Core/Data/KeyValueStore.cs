using System.ComponentModel.DataAnnotations;
namespace Ghost.Core.Data;

public class KeyValueStore
{
  [Key]
  public string Key { get; set; } = string.Empty;
  public string? Value { get; set; }
  public DateTime? ExpiresAt { get; set; }
  public DateTime CreatedAt { get; set; }
  public DateTime? UpdatedAt { get; set; }
}
