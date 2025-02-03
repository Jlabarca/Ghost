namespace Ghost.Core.Config;

/// <summary>
/// Configuration options for Ghost applications
/// </summary>
public class GhostOptions
{
  public string SystemId { get; set; } = "ghost";
  public bool UseRedis { get; set; } = false;
  public bool UsePostgres { get; set; } = false;
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
