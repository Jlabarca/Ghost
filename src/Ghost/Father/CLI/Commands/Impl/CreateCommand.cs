using Ghost.Core;
using Ghost.Templates;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ghost.Father.CLI.Commands;

public class CreateCommand : AsyncCommand<CreateCommand.Settings>
{
    private readonly TemplateManager _templateManager;

    public CreateCommand()
    {
        // Initialize template manager with built-in and user templates paths
        var templatesPath = Path.Combine(AppContext.BaseDirectory, "templates");
        var userTemplatesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ghost",
            "templates"
        );

        _templateManager = new TemplateManager(templatesPath);

        // Add user templates path if it exists
        if (Directory.Exists(userTemplatesPath))
        {
            _templateManager = new TemplateManager(userTemplatesPath);
        }
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[name]")]
        public string Name { get; set; }

        [CommandOption("--template")]
        public string Template { get; set; } = "full";

        [CommandOption("--description")]
        public string Description { get; set; }

        [CommandOption("--namespace")]
        public string Namespace { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Get output directory
            var outputPath = Directory.GetCurrentDirectory();

            // Validate template environment
            var template = await _templateManager.GetTemplateAsync(settings.Template);
            if (!await template.ValidateEnvironmentAsync())
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Template requirements not met. Please check dependencies.");
                return 1;
            }

            // Create project from template
            var projectDir = await _templateManager.CreateFromTemplateAsync(
                settings.Template,
                settings.Name,
                outputPath);

            // Initialize git repository if successful
            if (projectDir.Exists)
            {
                try
                {
                    LibGit2Sharp.Repository.Init(projectDir.FullName);
                    AnsiConsole.MarkupLine("[grey]Initialized Git repository[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] Failed to initialize Git repository: {ex.Message}");
                }
            }

            AnsiConsole.MarkupLine($"""

                [green]Created new Ghost app:[/] {settings.Name}

                To get started:
                [grey]cd[/] {settings.Name}
                [grey]dotnet restore[/]
                [grey]ghost run[/] {settings.Name}
                """);

            return 0;
        }
        catch (Exception ex) when (ex is GhostException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {ex.Message}");
            return 1;
        }
    }
}