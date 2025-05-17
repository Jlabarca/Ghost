using Ghost.Core.Data;
using Ghost.Core.Logging;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

namespace Ghost;

public static class G
{
    private static IGhostLogger? _logger;

    public static void Initialize(IGhostLogger logger)
    {
        _logger = logger;
        LogInfo($"{_logger.GetType().Name} initialized successfully.");
    }

    private static void Log(
        string message,
        LogLevel level = LogLevel.Information,
        Exception? ex = null,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
    {
        // EnsureInitialized(message); // Don't pass message here, avoid duplicate console logs if uninitialized
        if (!EnsureInitialized())
        {
            // Log to console if logger isn't ready, including the intended message
             Console.WriteLine($"[PRE-INIT {level.ToString().ToUpper()}] {message} (at {System.IO.Path.GetFileName(sourceFilePath)}:{sourceLineNumber}){(ex != null ? $"\n{ex}" : "")}");
             return;
        }
        _logger!.LogWithSource(message, level, ex, sourceFilePath, sourceLineNumber); // Pass the values explicitly
    }

    public static void LogInfo(
        string message,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        => Log(message, LogLevel.Information, null, sourceFilePath, sourceLineNumber);

    public static void LogDebug(
        string message,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        => Log(message, LogLevel.Debug, null, sourceFilePath, sourceLineNumber);

    public static void LogWarn(
        string message,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        => Log(message, LogLevel.Warning, null, sourceFilePath, sourceLineNumber);

    public static void LogError(
        string message,
        Exception? ex = null,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        => Log(message, LogLevel.Error, ex, sourceFilePath, sourceLineNumber);

    public static void LogError(
        Exception? ex,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        // Calls the other LogError overload, passing the captured source info
        => LogError(ex?.Message ?? "Unknown error", ex, sourceFilePath, sourceLineNumber);

    public static void LogCritical(
        string message,
        Exception? ex = null,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        => Log(message, LogLevel.Critical, ex, sourceFilePath, sourceLineNumber);

    public static void LogCritical(
        Exception? ex,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        // Calls the other LogCritical overload, passing the captured source info
        => LogCritical(ex?.Message ?? "Unknown error", ex, sourceFilePath, sourceLineNumber);

    public static void LogInfo(
        string message,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0,
        params object[] args)
    {
        LogInfo(string.Format(message, args), sourceFilePath, sourceLineNumber); // Pass captured info
    }

    public static void LogDebug(
        string message,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0,
        params object[] args)
    {
        LogDebug(string.Format(message, args), sourceFilePath, sourceLineNumber); // Pass captured info
    }

    public static void LogWarn(
        string message,
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0,
        params object[] args)
    {
        LogWarn(string.Format(message, args), sourceFilePath, sourceLineNumber); // Pass captured info
    }

    public static void LogError(
        string message,
        [CallerFilePath] string sourceFilePath = "", // Capture info here
        [CallerLineNumber] int sourceLineNumber = 0,
        params object[] args)
    {
        // Calls the LogError(string, Exception?, ...) overload
        LogError(string.Format(message, args), null, sourceFilePath, sourceLineNumber); // Pass captured info
    }

    public static void LogError(
        Exception ex,
        string message,
        [CallerFilePath] string sourceFilePath = "", // Capture info here
        [CallerLineNumber] int sourceLineNumber = 0,
        params object[] args)
    {
        // Calls the LogError(string, Exception?, ...) overload
        LogError(string.Format(message, args), ex, sourceFilePath, sourceLineNumber); // Pass captured info
    }

    // Modified EnsureInitialized to return a boolean and not log directly
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
        if (EnsureInitialized()) // Check if initialized before proceeding
        {
             _logger!.SetCache(cache);
        }
        // TODO:  if we want to throw an exception or log a warning if SetCache is called before Initialize
    }

    public static IGhostLogger GetLogger()
    {
        if (!EnsureInitialized())
        {
            // Throw an exception because returning null might cause NullReferenceExceptions later
            throw new InvalidOperationException("Ghost logger has not been initialized. Call Ghost.Initialize() first.");
        }
        return _logger!; // Can safely use null-forgiving operator here due to EnsureInitialized check
    }
}