namespace Ghost.Infrastructure.Data;

public class ConfigValue
{
  public string Value { get; set; }
  public string Type { get; set; }
  public DateTime LastModified { get; set; }

  public T GetTypedValue<T>()
  {
    if (typeof(T) == typeof(string))
      return (T)(object)Value;

    try
    {
      return System.Text.Json.JsonSerializer.Deserialize<T>(Value);
    }
    catch (Exception ex)
    {
      throw new GhostException(
          $"Failed to convert config value to type {typeof(T).Name}",
          ex,
          ErrorCode.ConfigurationError);
    }
  }

  public static ConfigValue Create<T>(T value)
  {
    var stringValue = typeof(T) == typeof(string)
        ? value.ToString()
        : System.Text.Json.JsonSerializer.Serialize(value);

    return new ConfigValue
    {
        Value = stringValue,
        Type = typeof(T).Name.ToLower(),
        LastModified = DateTime.UtcNow
    };
  }
}
