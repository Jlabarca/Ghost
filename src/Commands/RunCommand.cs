using Ghost.Infrastructure;
using Ghost.Services;
using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ghost.Commands;

public class RunCommand : AsyncCommand<RunCommand.Settings>
{
  private readonly AppRunner _appRunner;
  private readonly AliasManager _aliasManager;

  public class Settings : CommandSettings
  {
    [CommandOption("--url <URL>")]
    [Description("The repository URL to run the application from.")]
    public string Url { get; set; }

    [CommandArgument(0, "[Args...]")]
    [Description("Arguments to pass to the application")]
    public string[] Args { get; set; }
  }

  public RunCommand(AppRunner appRunner, AliasManager aliasManager)
  {
    _appRunner = appRunner;
    _aliasManager = aliasManager;
  }

  public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
  {
    try
    {
      var url = settings.Url;

      // If URL is not provided, check if it's an alias
      if (string.IsNullOrEmpty(url) && settings.Args?.Length > 0)
      {
        var potentialAlias = settings.Args[0];
        url = _aliasManager.GetAliasUrl(potentialAlias);
        if (url != null)
        {
          // Remove the alias from the args
          settings.Args = settings.Args.Skip(1).ToArray();
        }
      }

      if (string.IsNullOrEmpty(url))
      {
        throw new GhostException("No URL or valid alias provided");
      }

      AnsiConsole.MarkupLine($"Running application from [blue]{url}[/]");
      return await _appRunner.RunAsync(url, settings.Args ?? Array.Empty<string>());
    }
    catch (GhostException ex)
    {
      AnsiConsole.MarkupLine($"[red]Error:[/] {ex.UserMessage}");
      return 1;
    }
  }
}
