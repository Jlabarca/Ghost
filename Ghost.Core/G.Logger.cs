using System.Runtime.CompilerServices;
using Ghost.Data;
using Ghost.Logging;
using Microsoft.Extensions.Logging;
namespace Ghost;

public static class G
{
    private static IGhostLogger? _logger;
    private static ICache? _cache;
    private static LogLevel? _pendingLogLevel; // Store log level if set before initialization

    public static void Initialize(IGhostLogger logger)
    {
        _logger = logger;

        // Apply any pending log level that was set before initialization
        if (_pendingLogLevel.HasValue)
        {
            _logger.SetLogLevel(_pendingLogLevel.Value);
            _pendingLogLevel = null;
        }

        LogInfo($"{_logger.GetType().Name} initialized successfully.");
    }

    private static void Log(
            string message,
            LogLevel level = LogLevel.Information,
            Exception? ex = null,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (!EnsureInitialized())
        {
            // Log to console if logger isn't ready, including the intended message
            Console.WriteLine($"[PRE-INIT {level.ToString().ToUpper()}] {message} (at {Path.GetFileName(sourceFilePath)}:{sourceLineNumber}){(ex != null ? $"\n{ex}" : "")}");
            return;
        }
        _logger!.LogWithSource(message, level, ex, sourceFilePath, sourceLineNumber);
    }

    public static void LogInfo(
            string message,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        Log(message, LogLevel.Information, null, sourceFilePath, sourceLineNumber);
    }

    public static void LogDebug(
            string message,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        Log(message, LogLevel.Debug, null, sourceFilePath, sourceLineNumber);
    }

    public static void LogWarn(
            string message,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        Log(message, LogLevel.Warning, null, sourceFilePath, sourceLineNumber);
    }

    public static void LogError(
            string message,
            Exception? ex = null,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        Log(message, LogLevel.Error, ex, sourceFilePath, sourceLineNumber);
    }

    public static void LogError(
            Exception? ex,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        LogError(ex?.Message ?? "Unknown error", ex, sourceFilePath, sourceLineNumber);
    }

    public static void LogCritical(
            string message,
            Exception? ex = null,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        Log(message, LogLevel.Critical, ex, sourceFilePath, sourceLineNumber);
    }

    public static void LogCritical(
            Exception? ex,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        LogCritical(ex?.Message ?? "Unknown error", ex, sourceFilePath, sourceLineNumber);
    }

    public static void LogInfo(
            string message,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0,
            params object[] args)
    {
        LogInfo(string.Format(message, args), sourceFilePath, sourceLineNumber);
    }

    public static void LogDebug(
            string message,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0,
            params object[] args)
    {
        LogDebug(string.Format(message, args), sourceFilePath, sourceLineNumber);
    }

    public static void LogWarn(
            string message,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0,
            params object[] args)
    {
        LogWarn(string.Format(message, args), sourceFilePath, sourceLineNumber);
    }

    public static void LogError(
            string message,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0,
            params object[] args)
    {
        LogError(string.Format(message, args), null, sourceFilePath, sourceLineNumber);
    }

    public static void LogError(
            Exception ex,
            string message,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0,
            params object[] args)
    {
        LogError(string.Format(message, args), ex, sourceFilePath, sourceLineNumber);
    }

    private static bool EnsureInitialized()
    {
        if (_logger == null)
        {
            Console.WriteLine("Ghost logger not initialized. Call Ghost.Initialize() first. Subsequent log messages before initialization will appear with [PRE-INIT].");
            return false;
        }
        return true;
    }

    public static void SetCache(ICache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache), "Cache cannot be null");
        if (EnsureInitialized())
        {
            _logger!.SetCache(cache);
        }
    }

    public static IGhostLogger GetLogger()
    {
        if (!EnsureInitialized())
        {
            throw new InvalidOperationException("Ghost logger has not been initialized. Call Ghost.Initialize() first.");
        }
        return _logger!;
    }

    /// <summary>
    ///     Sets the log level. If the logger is not yet initialized, the level is stored and applied during initialization.
    /// </summary>
    /// <param name="logLevel">The log level to set</param>
    public static void SetLogLevel(LogLevel logLevel)
    {
        if (EnsureInitialized())
        {
            // Logger is ready, set the level immediately
            _logger!.SetLogLevel(logLevel);
            LogDebug($"Log level set to {logLevel}");
        }
        else
        {
            // Logger not ready, store for later
            _pendingLogLevel = logLevel;
            Console.WriteLine($"[PRE-INIT] Log level {logLevel} will be applied when logger is initialized.");
        }
    }

    /// <summary>
    ///     Gets the current cache instance, if available
    /// </summary>
    public static ICache? GetCache()
    {
        return _cache;
    }
}
