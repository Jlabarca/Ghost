
using Microsoft.Extensions.Logging;
namespace Ghost.Infrastructure.Logging;

public class GhostLoggerConfiguration
{
  public LogLevel LogLevel { get; set; } = LogLevel.Information;
  public string RedisKeyPrefix { get; set; } = "ghost:logs";
  public int RedisMaxLogs { get; set; } = 1000;
  public string LogsPath { get; set; } = "logs";
  public string OutputsPath { get; set; } = "outputs";
  public int MaxFilesPerDirectory { get; set; } = 100;
  public long MaxLogsSizeBytes { get; set; } = 100 * 1024 * 1024;    // 100MB
  public long MaxOutputSizeBytes { get; set; } = 500 * 1024 * 1024;  // 500MB
  public bool ShowSourceLocation { get; set; } = true;
}
