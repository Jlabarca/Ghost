using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Ghost.Infrastructure;
using Ghost.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ghost.Commands
{
    public class RunCommand : AsyncCommand<RunCommand.Settings>
    {
        private readonly AppRunner _appRunner;

        public class Settings : CommandSettings
        {
            [CommandOption("--url <URL>")]
            [Description("The repository URL to run the application from.")]
            public string Url { get; set; }

            [CommandArgument(0, "[Args...]")]
            [Description("Arguments to pass to the application")]
            public string[] Args { get; set; }
        }

        public RunCommand(AppRunner appRunner)
        {
            _appRunner = appRunner;
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            try
            {
                AnsiConsole.MarkupLine($"Running application from [blue]{settings.Url}[/]");
                return await _appRunner.RunAsync(settings.Url, settings.Args ?? Array.Empty<string>());
            }
            catch (GhostException ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.UserMessage}");
                return 1;
            }
        }
    }
}