using Spectre.Console;
using Spectre.Console.Cli;
using System.Reflection;

namespace Ghost.Father.CLI.Commands;

public class VersionCommand : Command<VersionCommand.Settings>
{
  public class Settings : CommandSettings;

  public override int Execute(CommandContext context, Settings settings)
  {
    var assembly = Assembly.GetExecutingAssembly();
    var version = assembly.GetName().Version;

    // Get build info from custom assembly attribute
    var buildInfo = assembly.GetCustomAttribute<BuildInfoAttribute>();
    var buildNumber = buildInfo?.BuildNumber ?? 0;

    // Display version information with styling
    AnsiConsole.MarkupLine($"[yellow]GhostFather[/] [green]v{version?.Major}.{version?.Minor}.{version?.Build}[/] [grey](build {buildNumber})[/]");

    // Get and display additional version information
    AnsiConsole.MarkupLine($"[grey]Running on .NET {Environment.Version}[/]");

    return 0;
  }
}
