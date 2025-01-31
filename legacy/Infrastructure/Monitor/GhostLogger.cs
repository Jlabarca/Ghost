using System.Collections.Concurrent;

namespace Ghost.Legacy.Infrastructure;

public class GhostLogger
{
    private readonly string _logsDirectory;
    private readonly ConcurrentDictionary<string, string> _activeLogFiles;
    private const int MAX_LOG_FILES = 100;

    public GhostLogger()
    {
        _logsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ghost",
            "logs"
        );
        Directory.CreateDirectory(_logsDirectory);
        _activeLogFiles = new ConcurrentDictionary<string, string>();
        CleanupOldLogs();
    }

    private void CleanupOldLogs()
    {
        var logFiles = Directory.GetFiles(_logsDirectory, "*.log")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Skip(MAX_LOG_FILES);

        foreach (var file in logFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    public string CreateLogFile(string instanceId)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var logFile = Path.Combine(_logsDirectory, $"{timestamp}_{instanceId}.log");
        _activeLogFiles.TryAdd(instanceId, logFile);
        return logFile;
    }

    public void Log(string instanceId, string message)
    {
        if (_activeLogFiles.TryGetValue(instanceId, out var logFile))
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logMessage = $"[{timestamp}] {message}{Environment.NewLine}";
            File.AppendAllText(logFile, logMessage);
        }
    }

    public string GetLogContent(string instanceId)
    {
        if (_activeLogFiles.TryGetValue(instanceId, out var logFile) && File.Exists(logFile))
        {
            return File.ReadAllText(logFile);
        }
        return string.Empty;
    }

    public IEnumerable<(DateTime timestamp, string content)> GetRecentLogs(int count = 10)
    {
        return Directory.GetFiles(_logsDirectory, "*.log")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .Take(count)
            .Select(f => (File.GetLastWriteTime(f), File.ReadAllText(f)));
    }
}