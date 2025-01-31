using Ghost.Infrastructure;
using Ghost.Infrastructure.Monitoring;
using Ghost.Legacy.Infrastructure;
using Ghost.Legacy.Infrastructure.Monitoring;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

// Core monitoring models - think of these as our sensor readings
namespace Ghost.Infrastructure.Monitoring;

public record ProcessStatus(
    string Id,
    string Name,
    string Status,
    int? Pid,
    int? Port,
    ProcessMetrics Metrics,
    DateTime LastHeartbeat,
    TimeSpan Uptime,
    int RestartCount
);

public class MonitorCommand : Command<MonitorCommand.Settings>
{
    private readonly MonitorSystem _monitor;
    private readonly GhostLogger _logger;

    public class Settings : CommandSettings
    {
        [CommandOption("--refresh-rate")]
        [Description("Refresh rate in seconds")]
        public int RefreshRate { get; set; } = 2;

        [CommandOption("--compact")]
        [Description("Show compact view")]
        public bool CompactView { get; set; }
    }

    public MonitorCommand(MonitorSystem monitor, GhostLogger logger)
    {
        _monitor = monitor;
        _logger = logger;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            return RunMonitorDisplay(settings);
        }
        catch (Exception ex)
        {
            _logger.Log("monitor-command", $"Monitor display error: {ex.Message}");
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private int RunMonitorDisplay(Settings settings)
    {
        var table = CreateMonitorTable(settings.CompactView);

        AnsiConsole.Clear();
        AnsiConsole.Live(table)
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .Cropping(VerticalOverflowCropping.Bottom)
            .Start(ctx => RunUpdateLoop(ctx, table, settings));

        return 0;
    }

    private Table CreateMonitorTable(bool compact)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title("[yellow]Ghost Process Monitor[/]")
            .AddColumn(new TableColumn("Name").Centered())
            .AddColumn(new TableColumn("Status").Centered())
            .AddColumn(new TableColumn("PID").Centered());

        if (!compact)
        {
            table
                .AddColumn(new TableColumn("Port").Centered())
                .AddColumn(new TableColumn("CPU %").Centered())
                .AddColumn(new TableColumn("Memory").Centered())
                .AddColumn(new TableColumn("Threads").Centered())
                .AddColumn(new TableColumn("Uptime").Centered())
                .AddColumn(new TableColumn("Last Heartbeat").Centered());
        }

        return table;
    }

    private void RunUpdateLoop(LiveDisplayContext ctx, Table table, Settings settings)
    {
        while (true)
        {
            try
            {
                UpdateTableContents(table, settings.CompactView);
                ctx.Refresh();
                Thread.Sleep(settings.RefreshRate * 1000);
            }
            catch (Exception ex)
            {
                _logger.Log("monitor-display", $"Display update error: {ex.Message}");
            }
        }
    }

    private void UpdateTableContents(Table table, bool compact)
    {
        table.Rows.Clear();

        var processes = _monitor.GetAllProcessStatus().Result;
        foreach (var proc in processes)
        {
            var row = CreateTableRow(proc, compact);
            table.AddRow(row);
        }
    }

    private static string[] CreateTableRow(ProcessStatus proc, bool compact)
    {
        var metrics = proc.Metrics;
        var color = proc.Status switch
        {
            "running" => "green",
            "stale" => "yellow",
            _ => "red"
        };

        var baseColumns = new[]
        {
            $"[blue]{proc.Name}[/]",
            $"[{color}]{proc.Status}[/]",
            proc.Pid?.ToString() ?? "-"
        };

        if (compact) return baseColumns;

        return baseColumns.Concat(new[]
        {
            proc.Port?.ToString() ?? "-",
            metrics != null ? $"{metrics.CpuPercentage:F1}%" : "-",
            metrics != null ? BytesToString(metrics.MemoryBytes) : "-",
            metrics?.ThreadCount.ToString() ?? "-",
            $"{proc.Uptime.TotalMinutes:F0}m",
            proc.LastHeartbeat.ToString("HH:mm:ss")
        }).ToArray();
    }

    private static string BytesToString(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double len = bytes;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:F1}{sizes[order]}";
    }
}