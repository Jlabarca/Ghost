using Ghost.Core.Data;
using Microsoft.Extensions.Logging;

namespace Ghost.Core.Logging;

public class GhostLoggerAdapter : ILogger
{
  private readonly DefaultGhostLogger _ghostLogger;

  public GhostLoggerAdapter(GhostLoggerConfiguration config, ICache cache)
  {
    _ghostLogger = new DefaultGhostLogger(config);
    _ghostLogger.SetCache(cache);
  }

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
      Exception exception, Func<TState, Exception, string> formatter)
  {
    var message = formatter(state, exception);
    _ghostLogger.LogWithSource(message, logLevel, exception);
  }
  public bool IsEnabled(LogLevel logLevel)
  {
    // Check if the log level is enabled
    return logLevel >= _ghostLogger.Config.LogLevel;
  }
  public IDisposable? BeginScope<TState>(TState state) where TState : notnull
  {
    // No scope management needed for Ghost logger
    return null;
  }
}
