using Ghost.Core.Data;
using Ghost.Core.Storage.Cache;
using Ghost.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace Ghost;

public static partial class G
{
  private static GhostLogger? _logger;

  public static void Initialize(GhostLogger logger)
  {
    _logger = logger;
  }

  public static void Log(string message, LogLevel level = LogLevel.Information, Exception? ex = null)
  {
    EnsureInitialized();
    _logger!.Log(level, ex, message);
  }

  public static void LogInfo(string message) => Log(message, LogLevel.Information);
  public static void LogDebug(string message) => Log(message, LogLevel.Debug);
  public static void LogWarn(string message) => Log(message, LogLevel.Warning);
  public static void LogError(string message, Exception? ex = null) => Log(message, LogLevel.Error, ex);
  public static void LogError(Exception? ex = null) => Log(ex.Message, LogLevel.Error, ex);
  public static void LogCritical(string message, Exception? ex) => Log(message, LogLevel.Critical, ex);
  public static void LogCritical(Exception? ex) => Log(ex.Message, LogLevel.Critical, ex);

  private static void EnsureInitialized()
  {
    if (_logger == null)
    {
      throw new InvalidOperationException("Ghost logger not initialized. Call Ghost.Initialize() first.");
    }
  }

  public static void LogInfo(string message, params object[] args)
  {
     LogInfo(string.Format(message, args));
  }

  public static void LogDebug(string message, params object[] args)
  {
     LogDebug(string.Format(message, args));
  }

  public static void LogWarn(string message, params object[] args)
  {
     LogWarn(string.Format(message, args));
  }

  public static void LogError(string message, params object[] args)
  {
     LogError(string.Format(message, args));
  }

  public static void LogError(Exception ex, string message, params object[] args)
  {
    LogError(string.Format(message, args), ex);
  }

  public static void SetCache(ICache cache)
  {
    _logger.SetCache(cache);
  }
}
