using System.Reflection;
using Spectre.Console;
using Spectre.Console.Cli;
namespace Ghost.Father.CLI.Commands;

public class VersionCommand : Command<VersionCommand.Settings>
{

    public override int Execute(CommandContext context, Settings settings)
    {
        Assembly? assembly = Assembly.GetExecutingAssembly();
        Version? version = assembly.GetName().Version;

        // Get build info from custom assembly attribute
        BuildInfoAttribute? buildInfo = assembly.GetCustomAttribute<BuildInfoAttribute>();
        int buildNumber = buildInfo?.BuildNumber ?? 0;

        // Display version information with styling
        AnsiConsole.MarkupLine($"[yellow]GhostFather[/] [green]v{version?.Major}.{version?.Minor}.{version?.Build}[/] [grey](build {buildNumber})[/]");

        // Get and display additional version information
        AnsiConsole.MarkupLine($"[grey]Running on .NET {Environment.Version}[/]");

        return 0;
    }
    public class Settings : CommandSettings;
}
