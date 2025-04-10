using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Ghost.SDK;

namespace Ghost;

public static partial class Ghost
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

  // Bus
  public static Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
    => GhostProcess.Instance.PublishAsync(channel, message, expiry);

  // Shutdown
  public static Task ShutdownAsync() => GhostProcess.Instance.ShutdownAsync();

}