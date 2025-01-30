using Ghost.Infrastructure;
using Ghost.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Commands;

public class CleanCommand : Command<CleanCommand.Settings>
{
    private readonly WorkspaceManager _workspaceManager;

    public class Settings : CommandSettings
    {
        [CommandOption("--all")]
        [Description("Clean all apps from the workspace")]
        public bool CleanAll { get; set; }

        [CommandOption("--older-than <DAYS>")]
        [Description("Clean apps older than specified days")]
        public int? OlderThan { get; set; }

        [CommandArgument(0, "[APP_NAME]")]
        [Description("Specific app to clean")]
        public string AppName { get; set; }
    }

    public CleanCommand(WorkspaceManager workspaceManager)
    {
        _workspaceManager = workspaceManager;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            if (settings.CleanAll)
            {
                var count = _workspaceManager.CleanAllApps();
                AnsiConsole.MarkupLine($"[green]Cleaned {count} apps from workspace[/]");
            }
            else if (settings.OlderThan.HasValue)
            {
                var count = _workspaceManager.CleanOlderThan(TimeSpan.FromDays(settings.OlderThan.Value));
                AnsiConsole.MarkupLine($"[green]Cleaned {count} apps older than {settings.OlderThan.Value} days[/]");
            }
            else if (!string.IsNullOrEmpty(settings.AppName))
            {
                _workspaceManager.CleanApp(settings.AppName);
                AnsiConsole.MarkupLine($"[green]Cleaned app {settings.AppName}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Please specify what to clean: --all, --older-than <days>, or provide an app name[/]");
                return 1;
            }

            return 0;
        }
        catch (GhostException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.UserMessage}");
            return 1;
        }
    }
}