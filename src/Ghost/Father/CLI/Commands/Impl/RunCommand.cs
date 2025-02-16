using Ghost.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ghost.Father.CLI.Commands;

public class RunCommand : AsyncCommand<RunCommand.Settings>
{
    private readonly IGhostBus _bus;

    public RunCommand(IGhostBus bus)
    {
        _bus = bus;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[name]")]
        public string Name { get; set; }

        [CommandOption("--args")]
        public string Args { get; set; }

        [CommandOption("--watch")]
        public bool Watch { get; set; }

        [CommandOption("--env")]
        public string[] Environment { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            var command = new SystemCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = "run",
                Parameters = new Dictionary<string, string>
                {
                    ["appId"] = settings.Name,
                    ["args"] = settings.Args ?? string.Empty,
                    ["watch"] = settings.Watch.ToString()
                }
            };

            // Add environment variables
            if (settings.Environment != null)
            {
                foreach (var env in settings.Environment)
                {
                    var parts = env.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        command.Parameters[$"env:{parts[0]}"] = parts[1];
                    }
                }
            }

            // Send command to daemon
            await _bus.PublishAsync("ghost:commands", command);

            // Subscribe for response
            var responseReceived = new TaskCompletionSource<bool>();
            await foreach (var response in _bus.SubscribeAsync<CommandResponse>("ghost:responses"))
            {
                if (response.CommandId == command.CommandId)
                {
                    if (response.Success)
                    {
                        AnsiConsole.MarkupLine($"[green]Started app:[/] {settings.Name}");

                        if (settings.Watch)
                        {
                            AnsiConsole.MarkupLine("[grey]Watching for changes...[/]");
                            // Keep running in watch mode
                            await Task.Delay(-1);
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Failed to start app:[/] {response.Error}");
                        return 1;
                    }
                    responseReceived.SetResult(true);
                    break;
                }
            }

            await responseReceived.Task;
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }
}