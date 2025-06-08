using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ghost.Data;
using Microsoft.Extensions.Logging;
namespace Ghost.Logging;

/// <summary>
///     DEFAULT Base implementation of Ghost logger without external UI dependencies
/// </summary>
public class GhostLogger : ILogger
{

    /// <summary>
    ///     Dictionary mapping log levels to their string representations
    /// </summary>
    protected static readonly Dictionary<LogLevel, string> LogLevelNames = new Dictionary<LogLevel, string>
    {
            {
                    LogLevel.Trace, "TRACE"
            },
            {
                    LogLevel.Debug, "DEBUG"
            },
            {
                    LogLevel.Information, "INFO"
            },
            {
                    LogLevel.Warning, "WARN"
            },
            {
                    LogLevel.Error, "ERROR"
            },
            {
                    LogLevel.Critical, "CRIT"
            }
    };

    private static readonly ConcurrentDictionary<string, DateTime> LastCleanupTime = new ConcurrentDictionary<string, DateTime>();
    private readonly GhostLoggerConfiguration _config;
    private readonly SemaphoreSlim _logLock = new SemaphoreSlim(1, 1);
    private readonly string _processId;
    private readonly ConcurrentQueue<LogEntry> _redisBuffer;
    protected ICache _cache;

    public GhostLogger(ICache cache, GhostLoggerConfiguration config)
    {
        _cache = cache;
        _config = config;
        _processId = Guid.NewGuid().ToString();
        _redisBuffer = new ConcurrentQueue<LogEntry>();

        Directory.CreateDirectory(_config.LogsPath);
        Directory.CreateDirectory(_config.OutputsPath);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return default(IDisposable?);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _config.LogLevel;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        string message = formatter(state, exception);
        string sourceFile = "";
        int sourceLine = 0;

        // Extract source info if available
        if (state is ILogState logState)
        {
            sourceFile = logState.SourceFilePath;
            sourceLine = logState.SourceLineNumber;
        }

        LogInternal(message, logLevel, exception, sourceFile, sourceLine);
    }

    public void SetCache(ICache cache)
    {
        _cache = cache;
    }

    // Use this method for direct logging
    public void LogWithSource(
            string message,
            LogLevel level = LogLevel.Information,
            Exception? exception = null,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (!IsEnabled(level))
        {
            return;
        }
        LogInternal(message, level, exception, sourceFilePath, sourceLineNumber);
    }

    protected virtual void LogInternal(
            string message,
            LogLevel level,
            Exception? exception,
            string sourceFilePath,
            int sourceLineNumber)
    {
        LogEntry? entry = new LogEntry
        {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message,
                Exception = exception?.ToString(),
                ProcessId = _processId,
                SourceFilePath = sourceFilePath,
                SourceLineNumber = sourceLineNumber
        };

        // Log to console (basic implementation, can be overridden)
        LogToConsole(entry);

        // Log detailed exception info to console if present
        if (exception != null)
        {
            LogExceptionToConsole(exception);
        }

        _logLock.Wait();
        try
        {
            // Log to Redis if available
            if (_cache != null)
            {
                LogToRedis(entry);
            }

            // Log to appropriate files
            if (level == LogLevel.Information)
            {
                LogToOutputFile(entry);
            }

            if (level >= LogLevel.Error)
            {
                LogToErrorFile(entry);
            }

            CleanupIfNeeded();
        }
        finally
        {
            _logLock.Release();
        }
    }

    /// <summary>
    ///     Logs a message to the console with basic formatting
    /// </summary>
    protected virtual void LogToConsole(LogEntry entry)
    {
        string? timestamp = entry.Timestamp.ToString("HH:mm:ss.fff");
        string? levelName = LogLevelNames.GetValueOrDefault(entry.Level, "UNKNOWN");
        string? logMessage = $"{timestamp} [{levelName}] {entry.Message}";

        // Add source location if available and configured
        if (_config.ShowSourceLocation && !string.IsNullOrEmpty(entry.SourceFilePath))
        {
            string fileName = Path.GetFileName(entry.SourceFilePath);
            logMessage += $" [{fileName}:{entry.SourceLineNumber}]";
        }

        // Simple console output without colors
        Console.WriteLine(logMessage);
    }

    /// <summary>
    ///     Logs an exception to the console
    /// </summary>
    protected virtual void LogExceptionToConsole(Exception exception)
    {
        Console.WriteLine();
        Console.WriteLine($"Exception: {exception.GetType().Name}");
        Console.WriteLine($"Message: {exception.Message}");
        Console.WriteLine($"StackTrace: {exception.StackTrace}");
        if (exception.InnerException != null)
        {
            Console.WriteLine($"Inner exception: {exception.InnerException.Message}");
        }
        Console.WriteLine();
    }

    private async void LogToRedis(LogEntry entry)
    {
        try
        {
            string? key = $"{_config.RedisKeyPrefix}:{_processId}";
            string? serialized = JsonSerializer.Serialize(entry);

            _redisBuffer.Enqueue(entry);
            while (_redisBuffer.Count > _config.RedisMaxLogs)
            {
                _redisBuffer.TryDequeue(out _);
            }

            await _cache.SetAsync(key, serialized);
        }
        catch
        {
            // Redis failure shouldn't affect the application
        }
    }

    private void LogToOutputFile(LogEntry entry)
    {
        string? outputFile = Path.Combine(
                _config.OutputsPath,
                $"{_processId}_output.log"
        );

        File.AppendAllLines(outputFile, new[]
        {
                FormatLogLine(entry)
        });
    }

    private void LogToErrorFile(LogEntry entry)
    {
        string? errorFile = Path.Combine(
                _config.LogsPath,
                $"{DateTime.UtcNow:yyyyMMdd}_errors.log"
        );

        File.AppendAllLines(errorFile, new[]
        {
                FormatLogLine(entry)
        });
    }

    private string FormatLogLine(LogEntry entry)
    {
        string? locationInfo = "";
        if (_config.ShowSourceLocation && !string.IsNullOrEmpty(entry.SourceFilePath))
        {
            string? fileName = Path.GetFileName(entry.SourceFilePath);
            locationInfo = $" [{fileName}:{entry.SourceLineNumber}]";
        }

        string? line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}]{locationInfo} {entry.Message}";
        if (entry.Exception != null)
        {
            line += Environment.NewLine + entry.Exception;
        }
        return line;
    }

    private void CleanupIfNeeded()
    {
        DateTime lastCleanup = LastCleanupTime.GetOrAdd(_processId, DateTime.UtcNow);
        if (DateTime.UtcNow - lastCleanup < TimeSpan.FromMinutes(5))
        {
            return;
        }

        try
        {
            LastCleanupTime[_processId] = DateTime.UtcNow;
            CleanupDirectory(_config.OutputsPath, _config.MaxOutputSizeBytes);
            CleanupDirectory(_config.LogsPath, _config.MaxLogsSizeBytes);
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private void CleanupDirectory(string path, long maxSizeBytes)
    {
        var files = Directory.GetFiles(path)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTime)
                .ToList();

        long totalSize = files.Sum(f => f.Length);
        if (totalSize <= maxSizeBytes)
        {
            return;
        }

        foreach (FileInfo? file in files.Skip(_config.MaxFilesPerDirectory))
        {
            try
            {
                file.Delete();
                totalSize -= file.Length;
                if (totalSize <= maxSizeBytes)
                {
                    break;
                }
            }
            catch
            {
                // Best effort deletion
            }
        }
    }

    private interface ILogState
    {
        string SourceFilePath { get; }
        int SourceLineNumber { get; }
    }

    private class SourceLogState<T> : ILogState
    {

        public SourceLogState(T state, string sourceFilePath, int sourceLineNumber)
        {
            State = state;
            SourceFilePath = sourceFilePath;
            SourceLineNumber = sourceLineNumber;
        }
        public T State { get; }
        public string SourceFilePath { get; }
        public int SourceLineNumber { get; }
    }
}
