using Ghost.Infrastructure;
using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Orchestration;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Commands;

public class ConfigCommand : AsyncCommand<ConfigCommand.Settings>
{
    private readonly IConfigManager _configManager;
    private readonly IDataAPI _dataApi;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[Action]")]
        [Description("Action to perform (get/set/list/remove)")]
        public string Action { get; set; }

        [CommandArgument(1, "[Key]")]
        [Description("Configuration key")]
        public string Key { get; set; }

        [CommandArgument(2, "[Value]")]
        [Description("Configuration value (for set action)")]
        public string Value { get; set; }

        [CommandOption("--system")]
        [Description("Manage system-wide configuration")]
        public bool IsSystem { get; set; }
    }

    public ConfigCommand(IConfigManager configManager, IDataAPI dataApi)
    {
        _configManager = configManager;
        _dataApi = dataApi;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            switch (settings.Action?.ToLower())
            {
                case "get":
                    if (string.IsNullOrEmpty(settings.Key))
                    {
                        AnsiConsole.MarkupLine("[red]Error:[/] Key is required for get action");
                        return 1;
                    }
                    await GetConfig(settings.Key, settings.IsSystem);
                    break;

                case "set":
                    if (string.IsNullOrEmpty(settings.Key) || string.IsNullOrEmpty(settings.Value))
                    {
                        AnsiConsole.MarkupLine("[red]Error:[/] Key and value are required for set action");
                        return 1;
                    }
                    await SetConfig(settings.Key, settings.Value, settings.IsSystem);
                    break;

                case "remove":
                    if (string.IsNullOrEmpty(settings.Key))
                    {
                        AnsiConsole.MarkupLine("[red]Error:[/] Key is required for remove action");
                        return 1;
                    }
                    await RemoveConfig(settings.Key, settings.IsSystem);
                    break;

                case "list":
                    await ListConfig(settings.IsSystem);
                    break;

                default:
                    AnsiConsole.MarkupLine("[red]Error:[/] Invalid action. Use get, set, remove, or list");
                    return 1;
            }

            return 0;
        }
        catch (Exception ex) when (ex is GhostException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task GetConfig(string key, bool isSystem)
    {
        var value = await _configManager.GetConfigAsync<ConfigValue>(key, isSystem ? null : "current");

        if (value != null)
        {
            var table = new Table()
                .AddColumn("Key")
                .AddColumn("Value")
                .AddColumn("Type")
                .AddColumn("Last Modified");

            table.AddRow(
                key,
                value.Value,
                value.Type,
                value.LastModified.ToString("yyyy-MM-dd HH:mm:ss")
            );

            AnsiConsole.Write(table);
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]No configuration found for key:[/] {key}");
        }
    }

    private async Task SetConfig(string key, string value, bool isSystem)
    {
        var configValue = new ConfigValue
        {
            Value = value,
            Type = "string",
            LastModified = DateTime.UtcNow
        };

        await _configManager.SetConfigAsync(key, configValue, isSystem ? null : "current");
        AnsiConsole.MarkupLine($"[green]Successfully set configuration:[/] {key} = {value}");
    }

    private async Task RemoveConfig(string key, bool isSystem)
    {
        await _configManager.DeleteConfigAsync(key, isSystem ? null : "current");
        AnsiConsole.MarkupLine($"[green]Successfully removed configuration:[/] {key}");
    }

    private async Task ListConfig(bool isSystem)
    {
        var prefix = isSystem ? "system:" : "current:";
        var keys = await _dataApi.GetKeysByPatternAsync($"config:{prefix}*");

        if (!keys.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No configurations found[/]");
            return;
        }

        var table = new Table()
            .AddColumn("Key")
            .AddColumn("Value")
            .AddColumn("Type")
            .AddColumn("Last Modified");

        foreach (var key in keys)
        {
            var value = await _configManager.GetConfigAsync<ConfigValue>(
                key.Replace($"config:{prefix}", ""),
                isSystem ? null : "current"
            );

            if (value != null)
            {
                table.AddRow(
                    key.Replace($"config:{prefix}", ""),
                    value.Value,
                    value.Type,
                    value.LastModified.ToString("yyyy-MM-dd HH:mm:ss")
                );
            }
        }

        AnsiConsole.Write(table);
    }
}
