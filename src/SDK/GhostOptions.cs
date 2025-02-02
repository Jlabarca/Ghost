
namespace Ghost.SDK;

/// <summary>
/// Configuration options for the Ghost SDK
/// Think of this as the "building specifications" - it defines how everything should be set up
/// </summary>
public class GhostOptions
{
  public string SystemId { get; set; } = "ghost";
  public bool UseRedis { get; set; } = false;  //LocalCacheClient if false
  public bool UsePostgres { get; set; } = false; //SqliteClient if false
  public string RedisConnectionString { get; set; } = "localhost:6379";
  public string PostgresConnectionString { get; set; } = "";
  public bool EnableMetrics { get; set; } = true;
  public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);
  public string DataDirectory { get; set; } = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "Ghost"
  );
  public Dictionary<string, string> AdditionalConfig { get; set; } = new();
}