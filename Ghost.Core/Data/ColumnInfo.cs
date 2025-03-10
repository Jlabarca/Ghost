namespace Ghost.Core.Data;

public class ColumnInfo
{

  public ColumnInfo(string name, string type, bool isNullable, bool isPrimaryKey, bool isAutoIncrement, string? defaultValue = null)
  {
    Name = name;
    Type = type;
    IsNullable = isNullable;
    IsPrimaryKey = isPrimaryKey;
    IsAutoIncrement = isAutoIncrement;
    DefaultValue = defaultValue;
  }
  public string Name { get; set; } = string.Empty;
  public string Type { get; set; } = string.Empty;
  public bool IsNullable { get; set; }
  public bool IsPrimaryKey { get; set; }
  public bool IsAutoIncrement { get; set; }
  public string? DefaultValue { get; set; }
}
