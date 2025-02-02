using Ghost.Infrastructure.Logging;
using Ghost.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Commands;

public class LogsCommand : AsyncCommand<LogsCommand.Settings>
{
    private readonly IRedisClient _redis;
    private readonly GhostLoggerConfiguration _config;
    private readonly CancellationTokenSource _cts = new();

    public class Settings : CommandSettings
    {
        [CommandOption("-f|--follow")]
        [Description("Follow log output")]
        public bool Follow { get; set; }

        [CommandOption("-n|--lines")]
        [Description("Number of lines to show")]
        [DefaultValue(50)]
        public int Lines { get; set; }

        [CommandOption("--level")]
        [Description("Filter by log level (debug,info,warn,error,critical)")]
        public string? Level { get; set; }

        [CommandOption("-p|--process")]
        [Description("Filter by process ID")]
        public string? ProcessId { get; set; }
    }

    public LogsCommand(IRedisClient redis, GhostLoggerConfiguration config)
    {
        _redis = redis;
        _config = config;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                _cts.Cancel();
            };

            if (settings.Follow)
            {
                await ShowLiveLogsAsync(settings);
            }
            else
            {
                await ShowHistoricalLogsAsync(settings);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task ShowHistoricalLogsAsync(Settings settings)
    {
        var table = CreateLogTable();
        var logs = new List<LogEntry>();

        await foreach (var entry in GetLogsAsync(settings.ProcessId))
        {
            if (ShouldShowLog(entry, settings.Level))
            {
                logs.Add(entry);
            }
        }

        // Show most recent logs first
        foreach (var entry in logs.OrderByDescending(l => l.Timestamp).Take(settings.Lines))
        {
            AddLogToTable(table, entry);
        }

        AnsiConsole.Write(table);
    }

    private async Task ShowLiveLogsAsync(Settings settings)
    {
        var table = CreateLogTable();
        AnsiConsole.Live(table)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Bottom)
            .Start(async ctx =>
            {
                await foreach (var entry in GetLogsAsync(settings.ProcessId))
                {
                    if (_cts.Token.IsCancellationRequested) break;

                    if (ShouldShowLog(entry, settings.Level))
                    {
                        AddLogToTable(table, entry);
                        ctx.Refresh();
                    }
                }
            });
    }

    private Table CreateLogTable()
    {
        var rest = 100 - 12 - 9 - 8;
        return new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumns(
                new TableColumn("Time").Width(12),
                new TableColumn("Level").Width(9),
                new TableColumn("Process").Width(8),
                new TableColumn("Message").Width(rest)
            );
    }

    private void AddLogToTable(Table table, LogEntry entry)
    {
        var (levelColor, levelText) = GetLevelStyle(entry.Level);

        table.AddRow(
            new Text(entry.Timestamp.ToString("HH:mm:ss.fff")),
            new Text(levelText).Centered(),//.Color(levelColor),
            new Text(entry.ProcessId[..6]),//.Color(Color.Blue),
            new Markup(EscapeMarkup(GetMessageWithException(entry)))
        );
    }

    private static (Color color, string text) GetLevelStyle(LogLevel level) => level switch
    {
        LogLevel.Critical => (Color.Red, "CRIT"),
        LogLevel.Error => (Color.Red, "ERROR"),
        LogLevel.Warning => (Color.Yellow, "WARN"),
        LogLevel.Information => (Color.Green, "INFO"),
        LogLevel.Debug => (Color.Blue, "DEBUG"),
        LogLevel.Trace => (Color.Grey, "TRACE"),
        _ => (Color.White, "NONE")
    };

    private static string GetMessageWithException(LogEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Exception))
            return entry.Message;

        var exceptionText = string.Join(
            Environment.NewLine,
            entry.Exception.Split(Environment.NewLine).Take(2)
        );

        return $"{entry.Message}\n[grey]{exceptionText}[/]";
    }

    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    private static bool ShouldShowLog(LogEntry entry, string? levelFilter)
    {
        if (string.IsNullOrEmpty(levelFilter)) return true;

        return levelFilter.ToLower() switch
        {
            "debug" => entry.Level >= LogLevel.Debug,
            "info" => entry.Level >= LogLevel.Information,
            "warn" => entry.Level >= LogLevel.Warning,
            "error" => entry.Level >= LogLevel.Error,
            "critical" => entry.Level >= LogLevel.Critical,
            _ => true
        };
    }

    private async IAsyncEnumerable<LogEntry> GetLogsAsync(string? processId)
    {
        var pattern = processId != null
            ? $"{_config.RedisKeyPrefix}:{processId}"
            : $"{_config.RedisKeyPrefix}:*";

        await foreach (var message in _redis.SubscribeAsync(pattern))
        {
            if (string.IsNullOrEmpty(message)) continue;

            var entry = System.Text.Json.JsonSerializer.Deserialize<LogEntry>(message);
            if (entry != null)
            {
                yield return entry;
            }
        }
    }
}