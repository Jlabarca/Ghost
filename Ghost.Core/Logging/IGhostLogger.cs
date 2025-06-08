using System.Runtime.CompilerServices;
using Ghost.Data;
using Microsoft.Extensions.Logging;
namespace Ghost.Logging;

/// <summary>
///     Interface for Ghost logger implementations, extending the standard ILogger
///     with additional Ghost-specific capabilities
/// </summary>
public interface IGhostLogger : ILogger
{
    /// <summary>
    ///     Log a message with source information
    /// </summary>
    /// <param name="message">The log message</param>
    /// <param name="level">Log level</param>
    /// <param name="exception">Optional exception</param>
    /// <param name="sourceFilePath">Source file path (auto-populated via CallerFilePath)</param>
    /// <param name="sourceLineNumber">Source line number (auto-populated via CallerLineNumber)</param>
    void LogWithSource(
            string message,
            LogLevel level = LogLevel.Information,
            Exception? exception = null,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0);

    /// <summary>
    ///     Updates the cache implementation used by the logger
    /// </summary>
    /// <param name="cache">New cache instance</param>
    void SetCache(ICache cache);
    void SetLogLevel(LogLevel initialLogLevel);
}
