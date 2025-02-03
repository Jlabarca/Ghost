using Ghost.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
namespace Ghost.SDK;

/// <summary>
/// Wrapper to adapt GhostLogger to ILogger interface
/// </summary>
internal class GhostLoggerWrapper : ILogger
{
  private readonly GhostLogger _ghostLogger;
  private readonly string _categoryName;

  public GhostLoggerWrapper(GhostLogger ghostLogger, string categoryName)
  {
    _ghostLogger = ghostLogger;
    _categoryName = categoryName;
  }

  public IDisposable BeginScope<TState>(TState state) where TState : notnull
  {
    return null; // Ghost logger doesn't support scopes currently
  }

  public bool IsEnabled(LogLevel logLevel)
  {
    return true; // Could be configurable in the future
  }

  public void Log<TState>(
      LogLevel logLevel,
      EventId eventId,
      TState state,
      Exception exception,
      Func<TState, Exception, string> formatter)
  {
    if (!IsEnabled(logLevel))
      return;

    var message = formatter(state, exception);
    _ghostLogger.Log(
        $"{_categoryName}: {message}",
        logLevel,
        exception);
  }
}
