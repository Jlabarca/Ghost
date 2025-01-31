using Ghost.Infrastructure;
using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Orchestration;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Commands;
public class AliasCommand : AsyncCommand<AliasCommand.Settings>
{
    private readonly IConfigManager _configManager;
    private readonly IDataAPI _dataApi;

    public class Settings : GhostCommandSettings
    {
        [CommandOption("--create")]
        [Description("Create new alias")]
        public string Create { get; set; }

        [CommandOption("--delete")]
        [Description("Delete alias")]
        public string Delete { get; set; }

        [CommandOption("--list")]
        [Description("List all aliases")]
        public bool List { get; set; }

        [CommandOption("--target")]
        [Description("Target process or URL")]
        public string Target { get; set; }
    }

    public AliasCommand(IConfigManager configManager, IDataAPI dataApi)
    {
        _configManager = configManager;
        _dataApi = dataApi;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            if (!string.IsNullOrEmpty(settings.Create))
            {
                await CreateAlias(settings.Create, settings.Target);
            }
            else if (!string.IsNullOrEmpty(settings.Delete))
            {
                await DeleteAlias(settings.Delete);
            }
            else if (settings.List)
            {
                await ListAliases();
            }

            return 0;
        }
        catch (GhostException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task CreateAlias(string alias, string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            throw new GhostException(
                "Target process or URL must be specified",
                ErrorCode.ValidationError);
        }

        await _configManager.SetConfigAsync($"aliases:{alias}", target);
        AnsiConsole.MarkupLine($"[green]Created alias[/] {alias} -> {target}");
    }

    private async Task DeleteAlias(string alias)
    {
        await _configManager.DeleteConfigAsync($"aliases:{alias}");
        AnsiConsole.MarkupLine($"[green]Deleted alias[/] {alias}");
    }

    private async Task ListAliases()
    {
        var aliases = await _dataApi.GetKeysByPatternAsync("aliases:*");
        var table = new Table()
            .AddColumn("Alias")
            .AddColumn("Target");

        foreach (var alias in aliases)
        {
            var target = await _configManager.GetConfigAsync<string>(alias);
            var aliasName = alias.Split(':')[1];
            table.AddRow(aliasName, target ?? "");
        }

        AnsiConsole.Write(table);
    }
}