using Ghost.Core.Data;
using Ghost.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghost;

public static partial class G
{
    private static IGhostLogger? _logger;

    /// <summary>
    /// Initialize the Ghost logger
    /// </summary>
    /// <param name="logger">Logger implementation</param>
    public static void Initialize(IGhostLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Log a message with the specified log level
    /// </summary>
    public static void Log(string message, LogLevel level = LogLevel.Information, Exception? ex = null)
    {
        EnsureInitialized();
        _logger!.LogWithSource(message, level, ex);
    }

    /// <summary>
    /// Log an informational message
    /// </summary>
    public static void LogInfo(string message) => Log(message, LogLevel.Information);

    /// <summary>
    /// Log a debug message
    /// </summary>
    public static void LogDebug(string message) => Log(message, LogLevel.Debug);

    /// <summary>
    /// Log a warning message
    /// </summary>
    public static void LogWarn(string message) => Log(message, LogLevel.Warning);

    /// <summary>
    /// Log an error message
    /// </summary>
    public static void LogError(string message, Exception? ex = null) => Log(message, LogLevel.Error, ex);

    /// <summary>
    /// Log an error message with exception
    /// </summary>
    public static void LogError(Exception? ex) => Log(ex?.Message ?? "Unknown error", LogLevel.Error, ex);

    /// <summary>
    /// Log a critical message with exception
    /// </summary>
    public static void LogCritical(string message, Exception? ex = null) => Log(message, LogLevel.Critical, ex);

    /// <summary>
    /// Log a critical message with exception
    /// </summary>
    public static void LogCritical(Exception? ex) => Log(ex?.Message ?? "Unknown error", LogLevel.Critical, ex);

    private static void EnsureInitialized()
    {
        if (_logger == null)
        {
            throw new InvalidOperationException("Ghost logger not initialized. Call Ghost.Initialize() first.");
        }
    }

    /// <summary>
    /// Log an informational message with format arguments
    /// </summary>
    public static void LogInfo(string message, params object[] args)
    {
        LogInfo(string.Format(message, args));
    }

    /// <summary>
    /// Log a debug message with format arguments
    /// </summary>
    public static void LogDebug(string message, params object[] args)
    {
        LogDebug(string.Format(message, args));
    }

    /// <summary>
    /// Log a warning message with format arguments
    /// </summary>
    public static void LogWarn(string message, params object[] args)
    {
        LogWarn(string.Format(message, args));
    }

    /// <summary>
    /// Log an error message with format arguments
    /// </summary>
    public static void LogError(string message, params object[] args)
    {
        LogError(string.Format(message, args));
    }

    /// <summary>
    /// Log an error message with exception and format arguments
    /// </summary>
    public static void LogError(Exception ex, string message, params object[] args)
    {
        LogError(string.Format(message, args), ex);
    }

    /// <summary>
    /// Update the cache implementation used by the logger
    /// </summary>
    public static void SetCache(ICache cache)
    {
        EnsureInitialized();
        _logger!.SetCache(cache);
    }
}