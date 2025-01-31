using Ghost.Infrastructure;
using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.ProcessManagement;
using Ghost.Infrastructure.Orchestration;
using Ghost.Infrastructure.Orchestration.Channels;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace Ghost.Commands;

public class RunCommand : AsyncCommand<RunCommand.Settings>
{
    private readonly IRedisManager _redisManager;
    private readonly IConfigManager _configManager;
    private readonly IDataAPI _dataApi;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[Name]")]
        [Description("Name or alias of the application to run")]
        public string Name { get; set; }

        [CommandOption("--url")]
        [Description("URL of the Git repository to run")]
        public string Url { get; set; }

        [CommandOption("--config")]
        [Description("Path to configuration file")]
        public string ConfigPath { get; set; }

        [CommandOption("--no-monitor")]
        [Description("Disable process monitoring")]
        public bool DisableMonitoring { get; set; }

        [CommandOption("--port")]
        [Description("Port to run the application on")]
        public int? Port { get; set; }

        [CommandOption("--env")]
        [Description("Environment variables in KEY=VALUE format")]
        public string[] Environment { get; set; }
    }

    public RunCommand(
        IRedisManager redisManager,
        IConfigManager configManager,
        IDataAPI dataApi)
    {
        _redisManager = redisManager;
        _configManager = configManager;
        _dataApi = dataApi;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Generate instance ID
            var instanceId = Guid.NewGuid().ToString("N");

            // Resolve URL from name/alias if needed
            var url = settings.Url;
            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(settings.Name))
            {
                url = await ResolveAliasUrl(settings.Name);
            }

            if (string.IsNullOrEmpty(url))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] No URL specified and no alias found");
                return 1;
            }

            // Load configuration
            var config = await LoadConfiguration(settings);

            // Create process metadata
            var metadata = new ProcessMetadata(
                Name: settings.Name ?? Path.GetFileNameWithoutExtension(url),
                Type: "dotnet",
                Version: "1.0.0",
                Environment: ParseEnvironmentVariables(settings.Environment),
                Configuration: config
            );

            // Set up process monitoring
            if (!settings.DisableMonitoring)
            {
                await SetupMonitoring(instanceId, metadata.Name);
            }

            // Create and configure process
            var process = new ProcessInfo(instanceId, metadata, CreateStartInfo(url, settings));

            // Handle process events
            process.OutputReceived += (s, e) => AnsiConsole.WriteLine(e.Data);
            process.ErrorReceived += (s, e) => AnsiConsole.MarkupLine($"[red]{e.Data}[/]");
            process.StatusChanged += async (s, e) => await HandleStatusChange(e);

            // Start the process
            await StartProcessWithProgress(process);

            // Wait for process to exit
            var statusSpinner = AnsiConsole.Status()
                .Start("Running...", ctx =>
                {
                    while (process.IsRunning)
                    {
                        ctx.Status = $"Running ({process.Uptime.TotalSeconds:F0}s)";
                        Thread.Sleep(1000);
                    }
                    return Task.CompletedTask;
                });

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task<string> ResolveAliasUrl(string name)
    {
        var alias = await _configManager.GetConfigAsync<string>($"aliases:{name}");
        if (alias == null)
        {
            throw new GhostException(
                $"Alias '{name}' not found",
                ErrorCode.ConfigurationError);
        }
        return alias;
    }

    private async Task<Dictionary<string, string>> LoadConfiguration(Settings settings)
    {
        var config = new Dictionary<string, string>();

        // Load from config file if specified
        if (!string.IsNullOrEmpty(settings.ConfigPath))
        {
            if (!File.Exists(settings.ConfigPath))
            {
                throw new GhostException(
                    $"Configuration file not found: {settings.ConfigPath}",
                    ErrorCode.ConfigurationError);
            }

            var fileConfig = await File.ReadAllTextAsync(settings.ConfigPath);
            var jsonConfig = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(fileConfig);
            foreach (var (key, value) in jsonConfig)
            {
                config[key] = value;
            }
        }

        // Add command line settings
        if (settings.Port.HasValue)
        {
            config["port"] = settings.Port.Value.ToString();
        }

        return config;
    }

    private Dictionary<string, string> ParseEnvironmentVariables(string[] env)
    {
        var vars = new Dictionary<string, string>();

        if (env != null)
        {
            foreach (var item in env)
            {
                var parts = item.Split('=', 2);
                if (parts.Length == 2)
                {
                    vars[parts[0]] = parts[1];
                }
            }
        }

        return vars;
    }

    private ProcessStartInfo CreateStartInfo(string url, Settings settings)
    {
        return new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{url}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private async Task SetupMonitoring(string processId, string name)
    {
        await _dataApi.SetDataAsync($"process:{processId}", new
        {
            Id = processId,
            Name = name,
            StartTime = DateTime.UtcNow
        });
    }

    private async Task StartProcessWithProgress(ProcessInfo process)
    {
        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var startTask = ctx.AddTask("[green]Starting process[/]");
                startTask.MaxValue = 100;

                try
                {
                    await process.StartAsync();
                    for (var i = 0; i < 100; i += 10)
                    {
                        startTask.Value = i;
                        await Task.Delay(100);
                    }
                    startTask.Value = 100;
                }
                catch
                {
                    startTask.Value = 100;
                    throw;
                }
            });
    }

    private async Task HandleStatusChange(ProcessStatusEventArgs e)
    {
        var statusColor = e.NewStatus switch
        {
            ProcessStatus.Running => "green",
            ProcessStatus.Warning => "yellow",
            ProcessStatus.Failed or ProcessStatus.Crashed => "red",
            _ => "blue"
        };

        AnsiConsole.MarkupLine(
            $"Process status changed: [{statusColor}]{e.NewStatus}[/]");

        await _redisManager.PublishStateAsync(e.ProcessId, new ProcessState(
            e.ProcessId,
            e.NewStatus.ToString(),
            new Dictionary<string, string>
            {
                ["previousStatus"] = e.OldStatus.ToString(),
                ["timestamp"] = e.Timestamp.ToString("o")
            }));
    }
}