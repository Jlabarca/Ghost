using Ghost.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Commands;

public class AliasCommand : Command<AliasCommand.Settings>
{
    private readonly ConfigManager _configManager;
    private readonly string _shellConfigPath;

    public class Settings : CommandSettings
    {
        [CommandOption("--create <ALIAS>")]
        [Description("The alias to create.")]
        public string Alias { get; set; }

        [CommandOption("--url <URL>")]
        [Description("The repository URL to associate with the alias.")]
        public string Url { get; set; }

        [CommandOption("--remove <ALIAS>")]
        [Description("Remove an existing alias.")]
        public string RemoveAlias { get; set; }

        [CommandOption("--list")]
        [Description("List all configured aliases.")]
        public bool List { get; set; }
    }

    public AliasCommand(ConfigManager configManager)
    {
        _configManager = configManager;
        _shellConfigPath = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents\\WindowsPowerShell\\Microsoft.PowerShell_profile.ps1")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bashrc");
    }

    private void CreatePowerShellProfile()
    {
        var profileDirectory = Path.GetDirectoryName(_shellConfigPath);
        if (!Directory.Exists(profileDirectory))
        {
            Directory.CreateDirectory(profileDirectory);
        }
        if (!File.Exists(_shellConfigPath))
        {
            File.WriteAllText(_shellConfigPath, "# PowerShell Profile\n");
        }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            if (settings.List)
            {
                var aliases = _configManager.GetAllAliases();
                if (aliases.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No aliases configured[/]");
                    return 0;
                }

                var table = new Table();
                table.AddColumn("Alias");
                table.AddColumn("Repository URL");

                foreach (var alias in aliases.OrderBy(a => a.Key))
                {
                    table.AddRow(
                        $"[blue]{alias.Key}[/]",
                        alias.Value
                    );
                }

                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[yellow]Configured Aliases[/]").RuleStyle("grey"));
                AnsiConsole.WriteLine();
                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
                return 0;
            }
            else if (!string.IsNullOrEmpty(settings.RemoveAlias))
            {
                RemoveAlias(settings.RemoveAlias);
                AnsiConsole.MarkupLine($"[green]Removed alias[/] [blue]{settings.RemoveAlias}[/]");
            }
            else if (!string.IsNullOrEmpty(settings.Alias) && !string.IsNullOrEmpty(settings.Url))
            {
                CreateAlias(settings.Alias, settings.Url);
                AnsiConsole.MarkupLine($"[green]Created alias[/] [blue]{settings.Alias}[/]");
                AnsiConsole.MarkupLine("[grey]Restart your terminal or run '. $PROFILE' to use the alias[/]");
            }
            else if (settings.Alias != null) // Special case for push command
            {
                CreateAlias(settings.Alias, _configManager.GetAlias(settings.Alias));
                AnsiConsole.MarkupLine($"[green]Created alias[/] [blue]{settings.Alias}[/]");
                AnsiConsole.MarkupLine("[grey]Restart your terminal or run '. $PROFILE' to use the alias[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[red]Please specify --list, or --create and --url, or --remove[/]");
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

    private void CreateAlias(string alias, string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new GhostException($"No URL found for alias '{alias}'", ErrorCode.AliasError);
        }

        // Save to config
        _configManager.SaveAlias(alias, url);

        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            CreatePowerShellProfile();

            // Create PowerShell function
            var aliasFunction = $@"
                function {alias} {{
                    ghost run --url {url} @args
                }}
            ";

            // Remove existing alias if present
            var lines = File.Exists(_shellConfigPath)
                ? File.ReadAllLines(_shellConfigPath)
                    .Where(line => !line.TrimStart().StartsWith($"function {alias} "))
                    .ToList()
                : [];

            // Add new alias
            lines.Add(aliasFunction);
            File.WriteAllLines(_shellConfigPath, lines);
        }
        else
        {
            // Create bash alias
            var aliasCommand = $"alias {alias}='ghost run --url {url}'";

            // Remove existing alias if present
            var lines = File.Exists(_shellConfigPath)
                ? File.ReadAllLines(_shellConfigPath)
                    .Where(line => !line.TrimStart().StartsWith($"alias {alias}="))
                    .ToList()
                : new List<string>();

            // Add new alias
            lines.Add(aliasCommand);
            File.WriteAllLines(_shellConfigPath, lines);
        }
    }

    private void RemoveAlias(string alias)
    {
        // Remove from config
        var url = _configManager.GetAlias(alias);
        if (url == null)
        {
            throw new GhostException($"Alias '{alias}' not found", ErrorCode.AliasError);
        }

        _configManager.SaveAlias(alias, null);

        // Remove from shell config
        if (File.Exists(_shellConfigPath))
        {
            var lines = File.ReadAllLines(_shellConfigPath)
                .Where(line => !line.TrimStart().StartsWith($"function {alias} ") &&
                              !line.TrimStart().StartsWith($"alias {alias}="))
                .ToList();
            File.WriteAllLines(_shellConfigPath, lines);
        }
    }
}