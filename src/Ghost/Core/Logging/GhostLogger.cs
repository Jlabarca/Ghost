using Ghost.Core.Storage;
using Ghost.Core.Storage.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Ghost.Infrastructure.Logging;

public class GhostLogger : ILogger
{
    private readonly string _processId;
    private readonly GhostLoggerConfiguration _config;
    private readonly ICache _cache;
    private readonly ConcurrentQueue<LogEntry> _redisBuffer;
    private readonly SemaphoreSlim _logLock = new(1, 1);

    public GhostLogger(ICache cache, GhostLoggerConfiguration config)
    {
        _cache = cache;
        _config = config;
        _processId = Guid.NewGuid().ToString();
        _redisBuffer = new ConcurrentQueue<LogEntry>();

        Directory.CreateDirectory(_config.LogsPath);
        Directory.CreateDirectory(_config.OutputsPath);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);
        Log(message, logLevel, exception);
    }

    public void Log(string message, LogLevel level, Exception? exception = null)
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message,
            Exception = exception?.ToString(),
            ProcessId = _processId
        };

        _logLock.Wait();
        try
        {
            // Log to Redis (GhostLogs system)
            LogToRedis(entry);

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
        var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] {entry.Message}";
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

public class GhostLoggerConfiguration
{
    public string RedisKeyPrefix { get; set; } = "ghost:logs";
    public int RedisMaxLogs { get; set; } = 1000;
    public string LogsPath { get; set; } = "logs";
    public string OutputsPath { get; set; } = "outputs";
    public int MaxFilesPerDirectory { get; set; } = 100;
    public long MaxLogsSizeBytes { get; set; } = 100 * 1024 * 1024;    // 100MB
    public long MaxOutputSizeBytes { get; set; } = 500 * 1024 * 1024;  // 500MB
}

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public LogLevel Level { get; set; }
    public string Message { get; set; } = "";
    public string? Exception { get; set; }
    public string ProcessId { get; set; } = "";
}

public static class GhostLoggerExtensions
{
    public static IServiceCollection AddGhostLogger(
        this IServiceCollection services,
        Action<GhostLoggerConfiguration>? configure = null)
    {
        var config = new GhostLoggerConfiguration();
        configure?.Invoke(config);

        services.AddSingleton(config);
        services.AddSingleton<ILogger, GhostLogger>();
        services.AddSingleton(sp =>
        {
            var logger = sp.GetRequiredService<ILogger>() as GhostLogger;
            //Ghost.Initialize(logger!);
            return logger;
        });

        return services;
    }
}