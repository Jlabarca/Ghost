using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class MonitorCommand : AsyncCommand<MonitorCommand.Settings>
{
    private readonly IGhostBus _bus;
    private readonly Table _processTable;
    private readonly Table _systemTable;
    private readonly Dictionary<string, ProcessMetrics> _lastMetrics = new();
    private bool _watching;

    public class Settings : CommandSettings
    {
        [CommandOption("--refresh")]
        [Description("Refresh interval in seconds")]
        [DefaultValue(5)]
        public int RefreshInterval { get; set; }

        [CommandOption("--no-clear")]
        [Description("Don't clear the screen between updates")]
        public bool NoClear { get; set; }

        [CommandOption("--process")]
        [Description("Monitor specific process")]
        public string? ProcessId { get; set; }
    }

    public MonitorCommand(IGhostBus bus)
    {
        _bus = bus;

        // Process table
        _processTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[blue]Processes[/]")
            .AddColumn("[grey]App[/]")
            .AddColumn("[grey]Status[/]")
            .AddColumn("[grey]Uptime[/]")
            .AddColumn("[grey]CPU[/]")
            .AddColumn("[grey]Memory[/]")
            .AddColumn("[grey]Type[/]")
            .AddColumn("[grey]Restarts[/]");

        // System status table
        _systemTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[blue]System Status[/]")
            .AddColumn("[grey]Component[/]")
            .AddColumn("[grey]Status[/]")
            .AddColumn("[grey]Info[/]");
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            _watching = true;
            if (!settings.NoClear) AnsiConsole.Clear();

            // Create layout
            var layout = new Layout("Root")
                .SplitRows(
                    new Layout("System", _systemTable),
                    new Layout("Processes", _processTable).Size(20)
                );

            // Start live display
            await AnsiConsole.Live(layout)
                .AutoClear(false)
                .Overflow(VerticalOverflow.Ellipsis)
                .Cropping(VerticalOverflowCropping.Bottom)
                .StartAsync(async ctx =>
                {
                    Task systemTask = MonitorSystemStatusAsync(ctx, settings);
                    Task processTask = MonitorProcessesAsync(ctx, settings);
                    await Task.WhenAll(systemTask, processTask);
                });

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
        finally
        {
            _watching = false;
        }
    }

    private async Task MonitorSystemStatusAsync(LiveDisplayContext ctx, Settings settings)
    {
        while (_watching)
        {
            try
            {
                UpdateSystemTable();
                ctx.Refresh();
                await Task.Delay(settings.RefreshInterval * 1000);
            }
            catch (Exception ex)
            {
                G.LogError("Error updating system status", ex);
                if (!settings.NoClear) AnsiConsole.Clear();
                AnsiConsole.MarkupLine($"[red]Error updating system status:[/] {ex.Message}");
                await Task.Delay(5000); // Wait before retrying
            }
        }
    }

    private async Task MonitorProcessesAsync(LiveDisplayContext ctx, Settings settings)
    {
        try
        {
            var filter = settings.ProcessId != null ? $"ghost:metrics:{settings.ProcessId}" : "ghost:metrics:#";

            await foreach (var metrics in _bus.SubscribeAsync<ProcessMetrics>(filter))
            {
                if (!_watching) break;

                UpdateProcessTable(metrics);
                ctx.Refresh();
            }
        }
        catch (Exception ex)
        {
            G.LogError("Error monitoring processes", ex);
            if (!settings.NoClear) AnsiConsole.Clear();
            AnsiConsole.MarkupLine($"[red]Error monitoring processes:[/] {ex.Message}");
        }
    }

    private void UpdateSystemTable()
    {
        _systemTable.Rows.Clear();

        // System metrics
        var process = Process.GetCurrentProcess();
        _systemTable.AddRow(
            "CPU",
            $"{process.TotalProcessorTime.TotalSeconds:F1}s",
            $"Threads: {process.Threads.Count}"
        );

        _systemTable.AddRow(
            "Memory",
            FormatBytes(process.WorkingSet64),
            $"Private: {FormatBytes(process.PrivateMemorySize64)}"
        );

        // GC metrics
        _systemTable.AddRow(
            "GC",
            $"Gen0: {GC.CollectionCount(0)}",
            $"Gen1: {GC.CollectionCount(1)}, Gen2: {GC.CollectionCount(2)}"
        );
    }

    private void UpdateProcessTable(ProcessMetrics metrics)
    {
        _lastMetrics[metrics.ProcessId] = metrics;
        _processTable.Rows.Clear();

        foreach (var (processId, lastMetrics) in _lastMetrics)
        {
            var age = DateTime.UtcNow - lastMetrics.Timestamp;
            if (age > TimeSpan.FromMinutes(1)) continue; // Skip stale entries

            _processTable.AddRow(
                processId,
                GetStatusMarkup(lastMetrics),
                $"{FormatUptime(age)}",
                $"{lastMetrics.CpuPercentage:F1}%",
                FormatBytes(lastMetrics.MemoryBytes),
                processId == "ghost" ? "[blue]daemon[/]" : "app",
                lastMetrics.ThreadCount.ToString()
            );
        }
    }

    private static string GetStatusMarkup(ProcessMetrics metrics)
    {
        if (metrics.CpuPercentage > 80) return "[red]high load[/]";
        if (metrics.CpuPercentage > 50) return "[yellow]busy[/]";
        if (metrics.CpuPercentage > 0) return "[green]running[/]";
        return "[grey]idle[/]";
    }

    private static string FormatUptime(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{ts.Days}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblBytes = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblBytes = bytes / 1024.0;
        }
        return $"{dblBytes:0.##}{suffix[i]}";
    }
}