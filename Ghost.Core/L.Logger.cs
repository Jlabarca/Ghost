using Ghost.Core.Data;
using Ghost.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Ghost;

public static class L
{
    private static IGhostLogger? _logger;

    public static void Initialize(IGhostLogger logger)
    {
        _logger = logger;
        _logger.LogInformation($"{_logger.GetType().Name} initialized");
    }

    public static void Log(string message, LogLevel level = LogLevel.Information, Exception? ex = null)
    {
        EnsureInitialized(message);
        _logger!.LogWithSource(message, level, ex);
    }


    public static void LogInfo(string message) => Log(message, LogLevel.Information);


    public static void LogDebug(string message) => Log(message, LogLevel.Debug);


    public static void LogWarn(string message) => Log(message, LogLevel.Warning);


    public static void LogError(string message, Exception? ex = null) => Log(message, LogLevel.Error, ex);

    public static void LogError(Exception? ex) => Log(ex?.Message ?? "Unknown error", LogLevel.Error, ex);

    public static void LogCritical(string message, Exception? ex = null) => Log(message, LogLevel.Critical, ex);

    public static void LogCritical(Exception? ex) => Log(ex?.Message ?? "Unknown error", LogLevel.Critical, ex);

    private static void EnsureInitialized(string? message = null)
    {
        if (_logger == null)
        {
            if (message != null)
            {
                Console.WriteLine("PrevInit: "+message);
                return;
            }

            Console.WriteLine("Ghost logger not initialized. Call Ghost.Initialize() first.");
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
        EnsureInitialized();
        _logger!.SetCache(cache);
    }
    public static IGhostLogger GetLogger()
    {
        EnsureInitialized();
        return _logger!;
    }
}