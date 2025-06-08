using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ghost.Data;
using Microsoft.Extensions.Logging;
namespace Ghost.Logging;

/// <summary>
///     Base implementation of Ghost logger without external UI dependencies
/// </summary>
public class DefaultGhostLogger : IGhostLogger
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

    /// <summary>
    ///     Dictionary mapping log levels to their console colors
    /// </summary>
    protected static readonly Dictionary<LogLevel, ConsoleColor> LogLevelColors = new Dictionary<LogLevel, ConsoleColor>
    {
            {
                    LogLevel.Trace, ConsoleColor.Gray
            },
            {
                    LogLevel.Debug, ConsoleColor.Cyan
            },
            {
                    LogLevel.Information, ConsoleColor.Green
            },
            {
                    LogLevel.Warning, ConsoleColor.Yellow
            },
            {
                    LogLevel.Error, ConsoleColor.Red
            },
            {
                    LogLevel.Critical, ConsoleColor.DarkRed
            }
    };

    private static readonly ConcurrentDictionary<string, DateTime> LastCleanupTime = new ConcurrentDictionary<string, DateTime>();
    private readonly SemaphoreSlim _logLock = new SemaphoreSlim(1, 1);

    private readonly string _processId;
    private readonly ConcurrentQueue<LogEntry> _redisBuffer;
    protected ICache _cache;

    public DefaultGhostLogger(GhostLoggerConfiguration config)
    {
        Config = config;
        _processId = Guid.NewGuid().ToString();
        _redisBuffer = new ConcurrentQueue<LogEntry>();

        Directory.CreateDirectory(Config.LogsPath);
        Directory.CreateDirectory(Config.OutputsPath);
    }
    public GhostLoggerConfiguration Config
    {
        get;
    }

    public void SetCache(ICache cache)
    {
        _cache = cache;
    }
    public void SetLogLevel(LogLevel initialLogLevel)
    {
        if (Config.LogLevel != initialLogLevel)
        {
            Config.LogLevel = initialLogLevel;
            LogInternal($"Log level set to {initialLogLevel}", LogLevel.Information, null, "", 0);
        }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return default(IDisposable?);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= Config.LogLevel;
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

    protected virtual void LogToConsole(LogEntry entry)
    {
        ConsoleColor originalColor = Console.ForegroundColor;
        try
        {
            string? timestamp = entry.Timestamp.ToString("HH:mm:ss.fff");
            string? levelName = LogLevelNames.GetValueOrDefault(entry.Level, "UNKNOWN");
            ConsoleColor levelColor = LogLevelColors.GetValueOrDefault(entry.Level, ConsoleColor.White);

            // 1. Timestamp (White)
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(timestamp);
            Console.Write(" ");

            // 2. Log Level (e.g., [DEBUG]) (Level-specific color)
            Console.ForegroundColor = levelColor;
            Console.Write($"[{levelName}]");
            Console.Write(" ");

            // 3. Log Message (White)
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(entry.Message);

            // 4. Source Location (e.g., [GhostFatherCLI.cs:39]) (Level-specific color)
            if (Config.ShowSourceLocation && !string.IsNullOrEmpty(entry.SourceFilePath))
            {
                Console.Write(" ");
                string filename = Path.GetFileName(entry.SourceFilePath);
                string absolutePath = Path.GetFullPath(entry.SourceFilePath);
                string fileUrl = new Uri(absolutePath).AbsoluteUri;

                Console.ForegroundColor = levelColor;

                // ANSI hyperlink for clickable source
                Console.Write("\x1b]8;;");
                Console.Write(fileUrl);
                Console.Write("\x1b\\");
                Console.Write($"[{filename}:{entry.SourceLineNumber}]");
                Console.Write("\x1b]8;;\x1b\\");
            }

            Console.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }

    protected virtual void LogExceptionToConsole(Exception exception)
    {
        ConsoleColor originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            Console.WriteLine($"Exception: {exception.GetType().Name}");
            Console.WriteLine($"Message: {exception.Message}");

            // Print stack trace with IDE-clickable format
            if (exception.StackTrace != null)
            {
                Console.WriteLine("Stack trace:");
                string[] stackTraceLines = exception.StackTrace.Split('\n');
                foreach (string? line in stackTraceLines)
                {
                    FormatStackTraceLine(line.Trim(), ConsoleColor.Red);
                }
            }

            if (exception.InnerException != null)
            {
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"Inner exception: {exception.InnerException.Message}");
                if (exception.InnerException.StackTrace != null)
                {
                    string[] stackTraceLines = exception.InnerException.StackTrace.Split('\n');
                    foreach (string? line in stackTraceLines)
                    {
                        FormatStackTraceLine(line.Trim(), ConsoleColor.White);
                    }
                }
            }
            Console.WriteLine();
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }

    private void FormatStackTraceLine(string line, ConsoleColor defaultLineColor)
    {
        ConsoleColor originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = defaultLineColor;
            if (line.Contains(" in "))
            {
                string[] parts = line.Split(new[]
                {
                        " in "
                }, StringSplitOptions.None);
                Console.Write($"  {parts[0]} in ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine(parts[1]);
            }
            else
            {
                Console.WriteLine($"  {line}");
            }
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }

    private async void LogToRedis(LogEntry entry)
    {
        try
        {
            string? key = $"{Config.RedisKeyPrefix}:{_processId}";
            string? serialized = JsonSerializer.Serialize(entry);

            _redisBuffer.Enqueue(entry);
            while (_redisBuffer.Count > Config.RedisMaxLogs)
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
                Config.OutputsPath,
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
                Config.LogsPath,
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
        if (Config.ShowSourceLocation && !string.IsNullOrEmpty(entry.SourceFilePath))
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
            CleanupDirectory(Config.OutputsPath, Config.MaxOutputSizeBytes);
            CleanupDirectory(Config.LogsPath, Config.MaxLogsSizeBytes);
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

        foreach (FileInfo? file in files.Skip(Config.MaxFilesPerDirectory))
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
