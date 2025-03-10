namespace Ghost.Core.Data;

public class TableAttribute : Attribute
{

  public TableAttribute(string name, string schema = "public")
  {
    Name = name;
    Schema = schema;
  }
  public string Name { get; }
  public string Schema { get; }
}
