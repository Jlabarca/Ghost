using Ghost.Core.Data;
using Microsoft.Extensions.Logging;

namespace Ghost.Core.Logging;

/// <summary>
/// Factory for creating Ghost logger instances
/// </summary>
public static class GhostLoggerFactory
{
    /// <summary>
    /// Creates a default Ghost logger instance
    /// </summary>
    /// <param name="cache">Cache implementation for distributed logging</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>A new DefaultGhostLogger instance</returns>
    public static IGhostLogger CreateDefaultLogger(ICache cache, Action<GhostLoggerConfiguration>? configure = null)
    {
        var config = new GhostLoggerConfiguration();
        configure?.Invoke(config);
        
        return new DefaultGhostLogger(cache, config);
    }
    
    /// <summary>
    /// Creates a null (no-op) Ghost logger instance for testing
    /// </summary>
    /// <returns>The NullGhostLogger singleton instance</returns>
    public static IGhostLogger CreateNullLogger()
    {
        return NullGhostLogger.Instance;
    }
    
    /// <summary>
    /// Creates a console logger that writes to standard output
    /// </summary>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>A new DefaultGhostLogger instance that logs to console</returns>
    public static IGhostLogger CreateConsoleLogger(Action<GhostLoggerConfiguration>? configure = null)
    {
        var config = new GhostLoggerConfiguration();
        configure?.Invoke(config);
        
        // Use a null cache since we don't need Redis functionality for console logging
        return new DefaultGhostLogger(null, config);
    }
    
    /// <summary>
    /// Creates a file logger that writes to the specified directory
    /// </summary>
    /// <param name="logsPath">Path to store log files</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>A new DefaultGhostLogger instance that logs to files</returns>
    public static IGhostLogger CreateFileLogger(string logsPath, Action<GhostLoggerConfiguration>? configure = null)
    {
        var config = new GhostLoggerConfiguration
        {
            LogsPath = logsPath,
            OutputsPath = Path.Combine(logsPath, "outputs")
        };
        
        configure?.Invoke(config);
        
        // Use a null cache since we don't need Redis functionality for file logging
        return new DefaultGhostLogger(null, config);
    }
}