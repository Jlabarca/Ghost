using Ghost.Core.Storage.Cache;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Ghost.Infrastructure.Logging;

public class GhostLogger : ILogger
{
    private readonly string _processId;
    private readonly GhostLoggerConfiguration _config;
    private ICache _cache;
    private readonly ConcurrentQueue<LogEntry> _redisBuffer;
    private readonly SemaphoreSlim _logLock = new(1, 1);
    private static readonly Dictionary<LogLevel, Color> LogLevelColors = new()
    {
        { LogLevel.Trace, Color.Grey },
        { LogLevel.Debug, Color.Blue },
        { LogLevel.Information, Color.Green },
        { LogLevel.Warning, Color.Yellow },
        { LogLevel.Error, Color.Red },
        { LogLevel.Critical, Color.Red1 }
    };

    public GhostLogger(ICache cache, GhostLoggerConfiguration config)
    {
        _cache = cache;
        _config = config;
        _processId = Guid.NewGuid().ToString();
        _redisBuffer = new ConcurrentQueue<LogEntry>();

        Directory.CreateDirectory(_config.LogsPath);
        Directory.CreateDirectory(_config.OutputsPath);
    }

    public void SetCache(ICache cache)
    {
        _cache = cache;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= _config.LogLevel;
    }
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        Log(message, logLevel, exception);
    }

    public void Log(string message,
            LogLevel level,
            Exception? exception = null,
            [CallerFilePath] string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0)
    {
        if (!IsEnabled(level))
            return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message,
            Exception = exception?.ToString(),
            ProcessId = _processId,
            SourceFilePath = sourceFilePath,
            SourceLineNumber = sourceLineNumber
        };

        // Log to console with Spectre formatting
        LogToConsole(entry);

        _logLock.Wait();
        try
        {
            // Log to Redis (GhostLogs system)
            if (_cache != null)
            {
                LogToRedis(entry);
            }

            // Log to file if it's process output
            if (level == LogLevel.Information)
            {
                LogToOutputFile(entry);
            }

            // Also log errors and critical messages to error file
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

    private void LogToConsole(LogEntry entry)
    {
        var color = LogLevelColors.GetValueOrDefault(entry.Level, Color.White);
        var timestamp = entry.Timestamp.ToString("HH:mm:ss.fff");
        var logLevel = entry.Level.ToString().ToUpper().PadRight(9);

        // Build the location info if enabled
        var locationInfo = "";
        // if (_config.ShowSourceLocation && !string.IsNullOrEmpty(entry.SourceFilePath))
        // {
        //     var fileName = Path.GetFileName(entry.SourceFilePath);
        //     locationInfo = $"{fileName}:{entry.SourceLineNumber}";
        // }

        // Create a clean single-line log message
        var logMessage = $"{timestamp} {logLevel} {locationInfo}{entry.Message}";

        // Apply colors using style instead of markup
        AnsiConsole.Write(new Text(logMessage, new Style(
            foreground: entry.Level switch
            {
                LogLevel.Trace => Color.Grey,
                LogLevel.Debug => Color.Blue,
                LogLevel.Information => Color.Green,
                LogLevel.Warning => Color.Yellow,
                LogLevel.Error => Color.Red,
                LogLevel.Critical => Color.Red1,
                _ => Color.White
            })));

        AnsiConsole.WriteLine();

        // If there's an exception, display it on the next line with proper formatting
        if (entry.Exception != null)
        {
            AnsiConsole.Write(new Panel(entry.Exception)
                .BorderColor(color)
                .RoundedBorder()
                .Padding(1, 1, 1, 1));
        }
    }

    private async void LogToRedis(LogEntry entry)
    {
        try
        {
            var key = $"{_config.RedisKeyPrefix}:{_processId}";
            var serialized = JsonSerializer.Serialize(entry);

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
            // Logs will still be written to files
        }
    }

    private void LogToOutputFile(LogEntry entry)
    {
        var outputFile = Path.Combine(
            _config.OutputsPath,
            $"{_processId}_output.log"
        );

        File.AppendAllLines(outputFile, new[] { FormatLogLine(entry) });
    }

    private void LogToErrorFile(LogEntry entry)
    {
        var errorFile = Path.Combine(
            _config.LogsPath,
            $"{DateTime.UtcNow:yyyyMMdd}_errors.log"
        );

        File.AppendAllLines(errorFile, new[] { FormatLogLine(entry) });
    }

    private string FormatLogLine(LogEntry entry)
    {
        var locationInfo = "";
        if (_config.ShowSourceLocation && !string.IsNullOrEmpty(entry.SourceFilePath))
        {
            var fileName = Path.GetFileName(entry.SourceFilePath);
            locationInfo = $" [{fileName}:{entry.SourceLineNumber}]";
        }

        var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}]{locationInfo} {entry.Message}";
        if (entry.Exception != null)
        {
            line += Environment.NewLine + entry.Exception;
        }
        return line;
    }

    private void CleanupIfNeeded()
    {
        var lastCleanup = LastCleanupTime.GetOrAdd(_processId, DateTime.UtcNow);
        if (DateTime.UtcNow - lastCleanup < TimeSpan.FromMinutes(5)) return;

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

        var totalSize = files.Sum(f => f.Length);
        if (totalSize <= maxSizeBytes) return;

        foreach (var file in files.Skip(_config.MaxFilesPerDirectory))
        {
            try
            {
                file.Delete();
                totalSize -= file.Length;
                if (totalSize <= maxSizeBytes) break;
            }
            catch
            {
                // Best effort deletion
            }
        }
    }

    private static readonly ConcurrentDictionary<string, DateTime> LastCleanupTime = new();
}

public static class LoggerExtensions
{
    public static void LogWithCaller(
            this ILogger logger,
            LogLevel logLevel,
            string message,
            [CallerLineNumber] int lineNumber = 0,
            [CallerFilePath] string filePath = "")
    {
        logger.Log(
                logLevel,
                new EventId(0),
                $"[{Path.GetFileName(filePath)}:{lineNumber}] {message}",
                null,
                (state, ex) => state.ToString()!);
    }
}