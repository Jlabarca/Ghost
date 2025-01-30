using Ghost.Infrastructure;
using Ghost.Services;
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ghost.Commands
{
    public class RunCommand : AsyncCommand<RunCommand.Settings>
    {
        private readonly AppRunner _appRunner;
        private readonly ConfigManager _configManager;
        private readonly ProcessRunner _processRunner;
        private readonly GhostLogger _logger;

        public class Settings : GhostCommandSettings
        {
            [CommandOption("--url <URL>")]
            [Description("The repository URL to run the application from.")]
            public string Url { get; set; }

            [CommandArgument(0, "[Args...]")]
            [Description("Arguments to pass to the application")]
            public string[] Args { get; set; }
        }

        public RunCommand(
            AppRunner appRunner,
            ConfigManager configManager,
            ProcessRunner processRunner,
            GhostLogger logger)
        {
            _appRunner = appRunner;
            _configManager = configManager;
            _processRunner = processRunner;
            _logger = logger;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            try
            {
                // Generate a unique instance ID for this run
                var instanceId = Guid.NewGuid().ToString("N");
                var logFile = _logger.CreateLogFile(instanceId);

                if (settings.Debug)
                {
                    AnsiConsole.MarkupLine($"[grey]Log file: {logFile}[/]");
                    AnsiConsole.MarkupLine($"[grey]Instance ID: {instanceId}[/]");
                    AnsiConsole.MarkupLine("[grey]Command execution details:[/]");
                    AnsiConsole.MarkupLine($"[grey]Working directory: {Directory.GetCurrentDirectory()}[/]");
                    AnsiConsole.MarkupLine($"[grey]Arguments: {string.Join(" ", settings.Args ?? Array.Empty<string>())}[/]");
                }

                var url = settings.Url;

                // If URL is not provided, check if it's an alias
                if (string.IsNullOrEmpty(url) && settings.Args?.Length > 0)
                {
                    var potentialAlias = settings.Args[0];
                    url = _configManager.GetAlias(potentialAlias);
                    if (url != null)
                    {
                        settings.Args = settings.Args.Skip(1).ToArray();
                    }
                }

                AnsiConsole.MarkupLine($"[green]Running application from:[/] [blue]{url}[/]");

                if (settings.Debug)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[grey]Debug mode enabled - showing detailed process information[/]");
                }

                // Create a new ProcessRunner with logging enabled
                var processRunner = new ProcessRunner(_logger, settings.Debug);

                // Create a new AppRunner with the debug-enabled ProcessRunner
                var appRunner = new AppRunner(processRunner);

                return await appRunner.RunAsync(url, settings.Args ?? Array.Empty<string>(), instanceId);
            }
            catch (GhostException ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.UserMessage}");
                return 1;
            }
        }
    }
}