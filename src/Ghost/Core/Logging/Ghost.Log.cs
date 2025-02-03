using Ghost.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Ghost;

public static partial class Ghost
{
  private static GhostLogger? _logger;

  public static void Initialize(GhostLogger logger)
  {
    _logger = logger;
  }

  public static void Log(string message, LogLevel level = LogLevel.Information, Exception? ex = null)
  {
    EnsureInitialized();
    _logger!.Log(message, level, ex);
  }

  public static void LogInfo(string message) => Log(message, LogLevel.Information);
  public static void LogDebug(string message) => Log(message, LogLevel.Debug);
  public static void LogWarn(string message) => Log(message, LogLevel.Warning);
  public static void LogError(string message, Exception? ex = null) => Log(message, LogLevel.Error, ex);
  public static void LogCritical(string message, Exception? ex = null) => Log(message, LogLevel.Critical, ex);

  private static void EnsureInitialized()
  {
    if (_logger == null)
    {
      throw new InvalidOperationException("Ghost logger not initialized. Call Ghost.Initialize() first.");
    }
  }
}
