
namespace Ghost.SDK;

/// <summary>
/// Configuration options for the Ghost SDK
/// Think of this as the "building specifications" - it defines how everything should be set up
/// </summary>
public class GhostOptions
{
  public string SystemId { get; set; } = "ghost";
  public string RedisConnectionString { get; set; }
  public string PostgresConnectionString { get; set; }
  public bool EnableMetrics { get; set; } = true;
  public TimeSpan MetricsInterval { get; set; } = TimeSpan.FromSeconds(5);
  public Dictionary<string, string> AdditionalConfig { get; set; } = new();
}
