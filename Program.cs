using Spectre.Console;
using Spectre.Console.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        var app = new CommandApp();
        app.Configure(config =>
        {
            config.AddCommand<HelloCommand>("hello")
                .WithDescription("Says hello!")
                .WithExample(new[] { "hello", "--name", "World" });
        });
        return app.Run(args);
    }
}

public class HelloCommand : Command<HelloCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--name")]
        public string Name { get; set; } = "World";
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        AnsiConsole.MarkupLine($"Hello [green]{settings.Name.EscapeMarkup()}[/]!");
        return 0;
    }
}