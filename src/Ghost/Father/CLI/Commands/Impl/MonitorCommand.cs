using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class MonitorCommand : AsyncCommand
{
    private readonly IGhostBus _bus;
    private readonly Table _processTable;
    private readonly Table _systemTable;
    private readonly Dictionary<string, ProcessMetrics> _lastMetrics = new();
    private bool _watching;

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

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            _watching = true;
            AnsiConsole.Clear();

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
                    Task systemTask = MonitorSystemStatusAsync(ctx);
                    Task processTask = MonitorProcessesAsync(ctx);

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

    private async Task MonitorSystemStatusAsync(LiveDisplayContext ctx)
    {
        while (_watching)
        {
            try
            {
                UpdateSystemTable();
                ctx.Refresh();
                await Task.Delay(5000); // Update every 5 seconds
            }
            catch (Exception ex)
            {
                G.LogError("Error updating system status", ex);
            }
        }
    }

    private void UpdateSystemTable()
    {
        _systemTable.Rows.Clear();

        // Check GhostFather Daemon
        bool isDaemonRunning = IsDaemonRunning();
        string daemonStatus = isDaemonRunning ? "[green]Running[/]" : "[red]Stopped[/]";

        // Check Installation
        var (isInstalled, installInfo) = CheckInstallation();
        string installStatus = isInstalled ? "[green]Installed[/]" : "[yellow]Partial[/]";

        // Check CLI
        bool isCliAccessible = IsCliAccessible();
        string cliStatus = isCliAccessible ? "[green]Available[/]" : "[red]Not Found[/]";

        _systemTable.AddRow("GhostFather Daemon", daemonStatus, GetDaemonInfo());
        _systemTable.AddRow("Installation", installStatus, installInfo);
        _systemTable.AddRow("CLI", cliStatus, GetCliInfo());
    }

    private async Task MonitorProcessesAsync(LiveDisplayContext ctx)
    {
        try
        {
            // Subscribe to metrics
            await foreach (var metrics in _bus.SubscribeAsync<ProcessMetrics>("ghost:metrics"))
            {
                if (!_watching) break;
                UpdateProcessTable(metrics);
                ctx.Refresh();
            }
        }
        catch (Exception ex)
        {
            G.LogError("Error monitoring processes", ex);
        }
    }

    private void UpdateProcessTable(ProcessMetrics metrics)
    {
        _lastMetrics[metrics.ProcessId] = metrics;
        _processTable.Rows.Clear();

        foreach (var (processId, lastMetrics) in _lastMetrics)
        {
            var uptime = DateTime.UtcNow - lastMetrics.Timestamp;
            var uptimeStr = FormatUptime(uptime);
            var cpuStr = $"{lastMetrics.CpuPercentage:F1}%";
            var memoryStr = FormatBytes(lastMetrics.MemoryBytes);

            // Special handling for GhostFather
            string type = processId == "GhostFather" ? "[blue]Daemon[/]" : "App";

            var row = new string[]
            {
                processId,
                GetStatusMarkup(lastMetrics),
                uptimeStr,
                cpuStr,
                memoryStr,
                type,
                lastMetrics.Gen0Collections.ToString()
            };

            _processTable.AddRow(row);
        }
    }

    private static string GetStatusMarkup(ProcessMetrics metrics)
    {
        if (metrics.CpuPercentage > 80)
            return "[red]high load[/]";
        if (metrics.CpuPercentage > 0)
            return "[green]running[/]";
        return "[yellow]idle[/]";
    }

    private static bool IsDaemonRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            // Check Windows service
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "query GhostFatherDaemon",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            return output.Contains("RUNNING");
        }
        else
        {
            // Check systemd service
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = "is-active ghost",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            return output.Trim() == "active";
        }
    }

    private static (bool isInstalled, string info) CheckInstallation()
    {
        var issues = new List<string>();
        bool hasService = false;
        bool hasExecutables = false;

        if (OperatingSystem.IsWindows())
        {
            // Check service installation
            using var sc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = "query GhostFatherDaemon",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            sc.Start();
            hasService = sc.StandardOutput.ReadToEnd().Contains("GhostFatherDaemon");

            // Check executable locations
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            hasExecutables = File.Exists(Path.Combine(programFiles, "Ghost", "ghost.exe"));
        }
        else
        {
            // Check systemd service
            using var systemctl = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "systemctl",
                    Arguments = "list-unit-files ghost.service",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            systemctl.Start();
            hasService = systemctl.StandardOutput.ReadToEnd().Contains("ghost.service");

            // Check executable locations
            hasExecutables = File.Exists("/usr/local/bin/ghost");
        }

        if (!hasService) issues.Add("service missing");
        if (!hasExecutables) issues.Add("executables missing");

        return (hasService && hasExecutables,
            issues.Count > 0 ? $"Issues: {string.Join(", ", issues)}" : "Fully installed");
    }

    private static bool IsCliAccessible()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
        if (paths == null) return false;

        foreach (var path in paths)
        {
            var exePath = Path.Combine(path, OperatingSystem.IsWindows() ? "ghost.exe" : "ghost");
            if (File.Exists(exePath))
                return true;
        }

        return false;
    }

    private static string GetDaemonInfo()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var exePath = Path.Combine(programFiles, "Ghost", "GhostFatherDaemon.exe");
            return File.Exists(exePath) ? exePath : "Not found";
        }
        else
        {
            return File.Exists("/usr/local/bin/ghostd") ? "/usr/local/bin/ghostd" : "Not found";
        }
    }

    private static string GetCliInfo()
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
        if (paths == null) return "PATH not set";

        foreach (var path in paths)
        {
            var exePath = Path.Combine(path, OperatingSystem.IsWindows() ? "ghost.exe" : "ghost");
            if (File.Exists(exePath))
                return exePath;
        }

        return "Not in PATH";
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{uptime.Days}d {uptime.Hours}h";
        if (uptime.TotalHours >= 1)
            return $"{uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalMinutes >= 1)
            return $"{uptime.Minutes}m {uptime.Seconds}s";
        return $"{uptime.Seconds}s";
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