using System.ComponentModel.DataAnnotations;
namespace Ghost.Core.Data;

public class ConfigEntry
{
  [Key]
  public string Key { get; set; } = string.Empty;

  public string? Value { get; set; }

  [Required]
  public string Type { get; set; } = string.Empty;

  public string? Schema { get; set; }

  public bool IsEncrypted { get; set; }

  public DateTime CreatedAt { get; set; }

  public DateTime? UpdatedAt { get; set; }
}
