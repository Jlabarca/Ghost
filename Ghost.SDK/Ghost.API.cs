using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;

namespace Ghost.SDK;

public static class Petter
{
  // Direct access to subsystems
  public static GhostConfig Config => GhostProcess.Instance.Config;
  public static IGhostData Data => GhostProcess.Instance.Data;
  public static IGhostBus Bus => GhostProcess.Instance.Bus;
  public static MetricsCollector Metrics => GhostProcess.Instance.Metrics;

  // Current app context
  public static GhostApp Current => GhostProcess.Instance.CurrentApp;

  // Initialization
  public static void Init(GhostApp app)
  {
    GhostProcess.Instance.Initialize(app);
  }

  public static void Init(GhostConfig config)
  {
    GhostProcess.Instance.Initialize(config);
  }

  // Metrics
  public static Task TrackMetricAsync(string name, double value, Dictionary<string, string> tags = null)
    => GhostProcess.Instance.TrackMetricAsync(name, value, tags);

  public static Task TrackEventAsync(string name, Dictionary<string, string> properties = null)
    => GhostProcess.Instance.TrackEventAsync(name, properties);

  // Data
  public static Task<int> ExecuteAsync(string sql, object param = null)
    => GhostProcess.Instance.ExecuteAsync(sql, param);

  public static Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
    => GhostProcess.Instance.QueryAsync<T>(sql, param);

  public static string GetSetting(string name, string defaultValue = null)
    => GhostProcess.Instance.GetSetting(name, defaultValue);

  // Bus
  public static Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
    => GhostProcess.Instance.PublishAsync(channel, message, expiry);

  // Shutdown
  public static Task ShutdownAsync() => GhostProcess.Instance.ShutdownAsync();

  // Logging methods
  public static void LogInfo(string message) => Log("INFO", message);
  public static void LogError(Exception ex, string message) => Log("ERROR", $"{message}: {ex.Message}");
  public static void LogWarn(string message) => Log("WARN", message);
  public static void LogDebug(string message) => Log("DEBUG", message);

  private static void Log(string level, string message)
  {
    // Implement logging
  }
}