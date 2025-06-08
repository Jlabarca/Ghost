using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
namespace Ghost.Father.CLI.Commands;

/// <summary>
///     Command to remove a Ghost app and its resources
/// </summary>
public class RemoveCommand : Command<RemoveCommand.Settings>
{

    public override int Execute(CommandContext context, Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Name))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] App name is required");
            return 1;
        }

        if (!settings.Force)
        {
            bool confirm = AnsiConsole.Confirm($"Are you sure you want to remove {settings.Name}?", false);
            if (!confirm)
            {
                return 0;
            }
        }

        try
        {
            // TODO: Implement app removal logic
            AnsiConsole.MarkupLine($"[yellow]Not implemented:[/] Would remove {settings.Name}");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[name]"), Description("Name of the Ghost app to remove")]
        public string Name { get; set; }

        [CommandOption("--force"), Description("Force removal without confirmation")]
        public bool Force { get; set; }
    }
}
