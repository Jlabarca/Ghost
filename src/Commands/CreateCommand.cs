using Ghost.Infrastructure;
using Ghost.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Commands;

public class CreateCommand : Command<CreateCommand.Settings>
    {
        private readonly ProjectGenerator _projectGenerator;

        public class Settings : CommandSettings
        {
            [CommandArgument(0, "<Name>")]
            [Description("The name of the project to create.")]
            public string Name { get; set; }
        }

        public CreateCommand(ProjectGenerator projectGenerator)
        {
            _projectGenerator = projectGenerator;
        }

        public override int Execute(CommandContext context, Settings settings)
        {
            try
            {
                var projectPath = Path.GetFullPath(settings.Name);

                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule($"[yellow]Creating project {settings.Name}[/]").RuleStyle("grey"));
                AnsiConsole.WriteLine();

                // Show project creation progress
                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Star)
                    .Start($"Creating project in [blue]{projectPath}[/]", ctx =>
                    {
                        _projectGenerator.CreateProject(settings.Name);
                        return;
                    });

                // Show project structure
                var root = new Tree($"[blue]{settings.Name}/[/]");
                var files = Directory.GetFiles(projectPath, "*", SearchOption.AllDirectories);
                var filesByDirectory = files
                        .OrderBy(f => f)
                        .GroupBy(f => Path.GetDirectoryName(Path.GetRelativePath(settings.Name, f)))
                        .OrderBy(g => g.Key);

                foreach (var dirGroup in filesByDirectory)
                {
                    IHasTreeNodes currentNode = root;

                    // Add directory path if it's not root
                    if (!string.IsNullOrEmpty(dirGroup.Key))
                    {
                        var pathParts = dirGroup.Key.Split(Path.DirectorySeparatorChar);
                        foreach (var part in pathParts)
                        {
                            currentNode = currentNode.AddNode($"[blue]{part}/[/]");
                        }
                    }

                    // Add files in this directory
                    foreach (var file in dirGroup)
                    {
                        var fileName = Path.GetFileName(file);
                        currentNode.AddNode($"[green]{fileName}[/]");
                    }
                }

                AnsiConsole.Write(root);
                AnsiConsole.WriteLine();

                // Show next steps
                AnsiConsole.Write(new Panel(
                    Align.Left(
                        new Markup($"""
                                    To get started:
                                    [grey]cd [/]{settings.Name}
                                    [grey]dotnet run[/] -- hello --name YourName
                                    """)
                    ))
                    .Header("[green]Project created successfully![/]")
                    .BorderColor(Color.Green));

                return 0;
            }
            catch (GhostException ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.UserMessage}");
                return 1;
            }
        }
    }
