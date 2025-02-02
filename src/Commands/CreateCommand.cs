using Ghost.Infrastructure;
using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Orchestration;
using Ghost.Infrastructure.Templates;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Commands;

public class CreateCommand : AsyncCommand<CreateCommand.Settings>
{
    private readonly IConfigManager _configManager;
    private readonly IDataAPI _dataApi;
    private readonly ProjectGenerator _generator;
    private readonly TemplateEngine _templateEngine;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<Name>")]
        [Description("Name of the application")]
        public string Name { get; set; }

        [CommandOption("--template")]
        [Description("Template to use")]
        public string Template { get; set; } = "default";

        [CommandOption("--output")]
        [Description("Output directory")]
        public string OutputDir { get; set; }

        [CommandOption("--description")]
        [Description("Project description")]
        public string Description { get; set; }

        [CommandOption("--no-git")]
        [Description("Skip Git initialization")]
        public bool SkipGit { get; set; }
    }

    public CreateCommand(
        IConfigManager configManager,
        IDataAPI dataApi,
        ProjectGenerator generator,
        TemplateEngine templateEngine)
    {
        _configManager = configManager;
        _dataApi = dataApi;
        _generator = generator;
        _templateEngine = templateEngine;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Show available templates if none specified
            if (string.IsNullOrEmpty(settings.Template))
            {
                ShowAvailableTemplates();
                return 0;
            }

            var outputDir = GetOutputDirectory(settings);
            if (Directory.Exists(outputDir))
            {
                throw new GhostException($"Directory already exists: {outputDir}");
            }

            // Start project creation
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[yellow]Creating {settings.Name}[/]")
                .RuleStyle("grey"));
            AnsiConsole.WriteLine();

            string projectPath = null;
            await AnsiConsole.Status()
                .StartAsync("Creating project...", async ctx =>
                {
                    ctx.Status = "Loading template...";
                    var template = await _templateEngine.LoadTemplateAsync(settings.Template);

                    // Prepare variables
                    var variables = new Dictionary<string, object>
                    {
                        ["description"] = settings.Description ?? template.Variables["defaultDescription"],
                        ["author"] = await _configManager.GetConfigAsync<string>("user.name") ?? "Ghost User"
                    };

                    // Generate project
                    ctx.Status = "Generating project files...";
                    projectPath = await _generator.GenerateProjectAsync(
                        settings.Template,
                        settings.Name,
                        outputDir,
                        variables);

                    // Initialize Git
                    if (!settings.SkipGit)
                    {
                        ctx.Status = "Initializing Git repository...";
                        await InitializeGitRepositoryAsync(projectPath);
                    }

                    // Save project metadata
                    ctx.Status = "Saving project metadata...";
                    await SaveProjectMetadataAsync(settings, projectPath, template);
                });

            // Show project structure
            await ShowProjectStructure(projectPath);

            // Show success message
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(
                Align.Left(new Markup($@"
Project created successfully!

To get started:
[grey]cd[/] {settings.Name}
[grey]dotnet run[/]

For more information, see the README.md file.
")))
                .Header("[green]Success![/]")
                .BorderColor(Color.Green));

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private void ShowAvailableTemplates()
    {
        var templates = _templateEngine.GetAvailableTemplates().ToList();

        var table = new Table()
            .AddColumn("Name")
            .AddColumn("Description")
            .AddColumn("Version")
            .AddColumn("Author")
            .AddColumn("Tags");

        foreach (var template in templates)
        {
            var tags = template.Tags != null
                ? string.Join(", ", template.Tags)
                : "";
            table.AddRow(
                $"[blue]{template.Name}[/]",
                template.Description ?? "",
                template.Version ?? "1.0.0",
                template.Author ?? "Unknown",
                tags);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(table)
            .Header("Available Templates")
            .BorderColor(Color.Blue));
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Use [grey]ghost create <name> --template <template>[/] to create a project");
        AnsiConsole.WriteLine();
    }

    private string GetOutputDirectory(Settings settings)
    {
        var basePath = string.IsNullOrEmpty(settings.OutputDir)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(settings.OutputDir);

        return Path.Combine(basePath, settings.Name);
    }

    private async Task InitializeGitRepositoryAsync(string projectPath)
    {
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "init",
                    WorkingDirectory = projectPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new GhostException(
                    $"Failed to initialize Git repository: {error}",
                    ErrorCode.GitError);
            }

            // Configure Git if user info is available
            var userName = await _configManager.GetConfigAsync<string>("user.name");
            var userEmail = await _configManager.GetConfigAsync<string>("user.email");

            if (!string.IsNullOrEmpty(userName))
            {
                await RunGitCommandAsync(
                    projectPath,
                    "config",
                    "user.name",
                    userName);
            }

            if (!string.IsNullOrEmpty(userEmail))
            {
                await RunGitCommandAsync(
                    projectPath,
                    "config",
                    "user.email",
                    userEmail);
            }

            // Add .gitignore if not already created by template
            var gitignorePath = Path.Combine(projectPath, ".gitignore");
            if (!File.Exists(gitignorePath))
            {
                await File.WriteAllTextAsync(gitignorePath, GetDefaultGitignore());
            }

            // Initial commit
            await RunGitCommandAsync(projectPath, "add", ".");
            await RunGitCommandAsync(
                projectPath,
                "commit",
                "-m",
                "Initial commit - Created with Ghost CLI");
        }
        catch (Exception ex) when (ex is not GhostException)
        {
            throw new GhostException(
                "Failed to initialize Git repository. Is Git installed?",
                ex,
                ErrorCode.GitError);
        }
    }

    private async Task RunGitCommandAsync(string workingDir, params string[] args)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = string.Join(" ", args),
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new GhostException(
                $"Git command failed: {error}",
                ErrorCode.GitError);
        }
    }

    private string GetDefaultGitignore() => @"
## .NET Core
bin/
obj/
*.user

## VS Code
.vscode/

## Visual Studio
.vs/
*.suo
*.user
*.userosscache
*.sln.docstates

## Rider
.idea/

## Ghost specific
.ghost/
*.log
";

    private async Task SaveProjectMetadataAsync(
        Settings settings,
        string projectPath,
        GhostTemplate template)
    {
        var metadata = new ProjectMetadata
        {
            Name = settings.Name,
            Template = template.Name,
            Version = template.Version,
            Description = settings.Description ?? template.Variables["defaultDescription"]?.ToString(),
            CreatedAt = DateTime.UtcNow,
            Path = projectPath,
            TemplateInfo = new TemplateInfo
            {
                Version = template.Version,
                Variables = template.Variables
            }
        };

        await _dataApi.SetDataAsync($"projects:{settings.Name}", metadata);
    }

    private async Task ShowProjectStructure(string projectPath)
    {
        var tree = new Tree($"[blue]{Path.GetFileName(projectPath)}[/]");
        BuildDirectoryTree(tree, projectPath);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(tree);
    }

    private void BuildDirectoryTree(IHasTreeNodes node, string path, int level = 0)
    {
        if (level > 3) return; // Limit depth for readability

        // Add directories
        foreach (var dir in Directory.GetDirectories(path)
            .Where(d => !Path.GetFileName(d).StartsWith("."))) // Skip hidden directories
        {
            var dirNode = node.AddNode($"[blue]{Path.GetFileName(dir)}[/]");
            BuildDirectoryTree(dirNode, dir, level + 1);
        }

        // Add files
        foreach (var file in Directory.GetFiles(path)
            .Where(f => !Path.GetFileName(f).StartsWith("."))) // Skip hidden files
        {
            var fileName = Path.GetFileName(file);
            var color = Path.GetExtension(file).ToLower() switch
            {
                ".cs" => "green",
                ".csproj" => "yellow",
                ".json" => "cyan",
                ".md" => "purple",
                _ => "grey"
            };
            node.AddNode($"[{color}]{fileName}[/]");
        }
    }
}

public class ProjectMetadata
{
    public string Name { get; set; }
    public string Template { get; set; }
    public string Version { get; set; }
    public string Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Path { get; set; }
    public TemplateInfo TemplateInfo { get; set; }
}

public class TemplateInfo
{
    public string Version { get; set; }
    public Dictionary<string, object> Variables { get; set; }
}