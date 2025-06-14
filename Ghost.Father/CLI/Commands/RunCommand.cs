using System.ComponentModel;
using Ghost.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
namespace Ghost.Father.CLI.Commands;

public class RunCommand : AsyncCommand<RunCommand.Settings>
{
    private const string GHOSTS_FOLDER = "ghosts";
    private readonly IGhostBus _bus;

    public RunCommand(IGhostBus bus)
    {
        _bus = bus;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Name))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] App name is required");
            return 1;
        }

        try
        {
            // Get the ghost installation directory
            string? ghostInstallDir = GetGhostInstallDirectory();

            // Locate the app in the ghosts folder
            string? ghostsFolder = Path.Combine(ghostInstallDir, GHOSTS_FOLDER);
            string? appFolder = Path.Combine(ghostsFolder, settings.Name);

            if (!Directory.Exists(appFolder))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] App '{settings.Name}' not found in {ghostsFolder}");
                return 1;
            }

            // Prepare the command to send to the daemon
            SystemCommand? command = new SystemCommand
            {
                    CommandId = Guid.NewGuid().ToString(),
                    CommandType = "run",
                    Parameters = new Dictionary<string, string>
                    {
                            ["appId"] = settings.Name,
                            ["appPath"] = appFolder,
                            ["args"] = settings.Args ?? string.Empty,
                            ["watch"] = settings.Watch.ToString()
                    }
            };

            // Add environment variables
            if (settings.Environment != null)
            {
                foreach (string? env in settings.Environment)
                {
                    string[]? parts = env.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        command.Parameters[$"env:{parts[0]}"] = parts[1];
                    }
                }
            }

            // Send command to daemon
            await _bus.PublishAsync("ghost:commands", command);

            // Subscribe for response
            var responseReceived = new TaskCompletionSource<bool>();
            try
            {
                await foreach (CommandResponse? response in _bus.SubscribeAsync<CommandResponse>("ghost:responses"))
                {
                    if (response.CommandId == command.CommandId)
                    {
                        if (response.Success)
                        {
                            AnsiConsole.MarkupLine($"[green]Started app:[/] {settings.Name}");

                            if (settings.Watch)
                            {
                                AnsiConsole.MarkupLine("[grey]Watching for changes...[/]");

                                // Create a monitoring status
                                await ShowMonitoringStatus(settings.Name);
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine($"[red]Failed to start app:[/] {response.Error}");
                            return 1;
                        }

                        responseReceived.SetResult(true);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when watching is cancelled
            }

            // Wait for response or timeout
            var responseTask = responseReceived.Task;
            if (await Task.WhenAny(responseTask, Task.Delay(10000)) != responseTask)
            {
                AnsiConsole.MarkupLine("[yellow]Warning:[/] No response received from daemon. The app may still be starting.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task ShowMonitoringStatus(string appName)
    {
        // Subscribe to metrics for this app
        string? metricChannel = $"ghost:metrics:{appName}";

        AnsiConsole.MarkupLine($"[blue]Monitoring app:[/] {appName}");
        AnsiConsole.MarkupLine("[grey]Press Ctrl+C to exit[/]");

        CancellationTokenSource? cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var lastMetrics = new Dictionary<string, object>();
        DateTime startTime = DateTime.UtcNow;

        try
        {
            await foreach (dynamic? metrics in _bus.SubscribeAsync<dynamic>(metricChannel, cts.Token))
            {
                if (metrics != null)
                {
                    AnsiConsole.Clear();
                    AnsiConsole.MarkupLine($"[blue]Monitoring app:[/] {appName}");
                    AnsiConsole.MarkupLine($"[grey]Running for:[/] {DateTime.UtcNow - startTime:hh\\:mm\\:ss}");
                    AnsiConsole.MarkupLine("[grey]Press Ctrl+C to exit[/]");

                    // Display metrics
                    Table? table = new Table().Border(TableBorder.Rounded);
                    table.AddColumn("Metric");
                    table.AddColumn("Value");

                    foreach (var metric in (IDictionary<string, object>)metrics.Metrics)
                    {
                        string value = FormatMetricValue(metric.Value);
                        table.AddRow(metric.Key, value);
                        lastMetrics[metric.Key] = metric.Value;
                    }

                    AnsiConsole.Write(table);
                }

                await Task.Delay(1000, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation when Ctrl+C is pressed
            AnsiConsole.MarkupLine("[grey]Monitoring stopped[/]");
        }
    }

    private string FormatMetricValue(object value)
    {
        if (value is double d)
        {
            if (d > 1_000_000)
            {
                return $"{d / 1_000_000:N2} M";
            }
            if (d > 1_000)
            {
                return $"{d / 1_000:N2} K";
            }
            return $"{d:N2}";
        }

        if (value is long l)
        {
            if (l > 1_000_000_000)
            {
                return $"{l / 1_000_000_000:N2} GB";
            }
            if (l > 1_000_000)
            {
                return $"{l / 1_000_000:N2} MB";
            }
            if (l > 1_000)
            {
                return $"{l / 1_000:N2} KB";
            }
            return $"{l:N0}";
        }

        return value?.ToString() ?? "N/A";
    }

    private string GetGhostInstallDirectory()
    {
        // First check the environment variable
        string? envDir = Environment.GetEnvironmentVariable("GHOST_INSTALL");
        if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir))
        {
            return envDir;
        }

        // Fall back to local application data
        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? ghostDir = Path.Combine(localAppData, "Ghost");

        // Create if it doesn't exist
        if (!Directory.Exists(ghostDir))
        {
            Directory.CreateDirectory(ghostDir);
        }

        return ghostDir;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[name]"), Description("Name of the Ghost app to run")]
        public string Name { get; set; }

        [CommandOption("--args"), Description("Arguments to pass to the app")]
        public string Args { get; set; }

        [CommandOption("--watch"), Description("Watch for changes and restart the app")]
        public bool Watch { get; set; }

        [CommandOption("--env"), Description("Environment variables in format KEY=VALUE")]
        public string[] Environment { get; set; }
    }
}
