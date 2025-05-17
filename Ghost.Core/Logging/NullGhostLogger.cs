using Ghost.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace Ghost.Core.Logging;

/// <summary>
/// A no-op implementation of IGhostLogger for testing purposes
/// </summary>
public class NullGhostLogger : IGhostLogger
{

  private static readonly NullGhostLogger _instance = new();

  public static NullGhostLogger Instance => _instance;

  public NullGhostLogger() { }

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => false;

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
  {
    // Do nothing
  }

  public void LogWithSource(string message, LogLevel level = LogLevel.Information, Exception? exception = null, [CallerFilePath] string sourceFilePath = "", [CallerLineNumber] int sourceLineNumber = 0)
  {
    // Do nothing
  }

  public void SetCache(ICache cache)
  {
    // Do nothing
  }
  public ILogger<T>? CreateLogger<T>()
  {
    // Return a null logger for the specified type
    return new NullLogger<T>();
  }
}

/// <summary>
/// Extension methods for NullGhostLogger
/// </summary>
public static class NullGhostLoggerExtensions
{
  /// <summary>
  /// Adds a null (no-op) Ghost logger to the service collection for testing
  /// </summary>
  public static IServiceCollection AddNullGhostLogger(this IServiceCollection services)
  {
    // Register the singleton instance
    services.AddSingleton<IGhostLogger>(NullGhostLogger.Instance);

    // Also register as standard ILogger
    services.AddSingleton<ILogger>(NullGhostLogger.Instance);

    // Initialize G with the null logger
    G.Initialize(NullGhostLogger.Instance);

    return services;
  }
}
