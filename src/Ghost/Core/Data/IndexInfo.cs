namespace Ghost.Core.Data;

public class IndexInfo
{
  public string Name { get; set; } = string.Empty;
  public string[] Columns { get; set; } = Array.Empty<string>();
  public bool IsUnique { get; set; }
  public string? Filter { get; set; }
}
