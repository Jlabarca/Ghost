using Ghost.Infrastructure;
using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Orchestration;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Commands;

public class CreateCommand : AsyncCommand<CreateCommand.Settings>
{
    private readonly IConfigManager _configManager;
    private readonly IDataAPI _dataApi;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<Name>")]
        [Description("The name of the project to create")]
        public string Name { get; set; }

        [CommandOption("--template")]
        [Description("Template to use (default, web, console, worker)")]
        public string Template { get; set; } = "default";

        [CommandOption("--output")]
        [Description("Output directory (defaults to current directory)")]
        public string OutputDir { get; set; }

        [CommandOption("--package-name")]
        [Description("NuGet package name (defaults to project name)")]
        public string PackageName { get; set; }

        [CommandOption("--description")]
        [Description("Project description")]
        public string Description { get; set; }

        [CommandOption("--author")]
        [Description("Project author")]
        public string Author { get; set; }

        [CommandOption("--no-git")]
        [Description("Skip Git initialization")]
        public bool SkipGit { get; set; }
    }

    public CreateCommand(IConfigManager configManager, IDataAPI dataApi)
    {
        _configManager = configManager;
        _dataApi = dataApi;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Validate project name
            if (!IsValidProjectName(settings.Name))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Invalid project name. Use only letters, numbers, and underscores");
                return 1;
            }

            // Resolve output directory
            var outputDir = GetOutputDirectory(settings);
            if (Directory.Exists(outputDir))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Directory already exists: {outputDir}");
                return 1;
            }

            // Load template
            var template = await LoadTemplate(settings.Template);
            if (template == null)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Template not found: {settings.Template}");
                return 1;
            }

            // Show project creation progress
            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var progressTasks = SetupProgressTasks(ctx);

                    // Create project directory
                    progressTasks["structure"].StartTask();
                    Directory.CreateDirectory(outputDir);
                    progressTasks["structure"].Increment(50);

                    // Generate project files
                    progressTasks["files"].StartTask();
                    await GenerateProjectFiles(settings, template, outputDir);
                    progressTasks["files"].Increment(100);

                    // Initialize Git repository
                    if (!settings.SkipGit)
                    {
                        progressTasks["git"].StartTask();
                        await InitializeGitRepository(outputDir);
                        progressTasks["git"].Increment(100);
                    }

                    // Save project metadata
                    progressTasks["metadata"].StartTask();
                    await SaveProjectMetadata(settings, outputDir);
                    progressTasks["metadata"].Increment(100);

                    progressTasks["structure"].Increment(50);
                });

            // Show project structure
            await ShowProjectStructure(outputDir);

            // Show success message
            ShowSuccessMessage(settings, outputDir);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private bool IsValidProjectName(string name)
    {
        return !string.IsNullOrEmpty(name) &&
               System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9_]+$");
    }

    private string GetOutputDirectory(Settings settings)
    {
        var basePath = string.IsNullOrEmpty(settings.OutputDir)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(settings.OutputDir);

        return Path.Combine(basePath, settings.Name);
    }

    private async Task<ProjectTemplate> LoadTemplate(string templateName)
    {
        var template = await _dataApi.GetDataAsync<ProjectTemplate>($"templates:{templateName}");
        if (template == null)
        {
            // Load built-in template
            template = templateName.ToLower() switch
            {
                "default" => CreateDefaultTemplate(),
                "web" => CreateWebTemplate(),
                "console" => CreateConsoleTemplate(),
                "worker" => CreateWorkerTemplate(),
                _ => null
            };
        }
        return template;
    }

    private Dictionary<string, ProgressTask> SetupProgressTasks(ProgressContext context)
    {
        return new Dictionary<string, ProgressTask>
        {
            ["structure"] = context.AddTask("[green]Creating project structure[/]"),
            ["files"] = context.AddTask("[green]Generating project files[/]"),
            ["git"] = context.AddTask("[green]Initializing Git repository[/]"),
            ["metadata"] = context.AddTask("[green]Saving project metadata[/]")
        };
    }

    private async Task GenerateProjectFiles(Settings settings, ProjectTemplate template, string outputDir)
    {
        foreach (var (path, content) in template.Files)
        {
            var fullPath = Path.Combine(outputDir, path);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var processedContent = ProcessTemplateContent(content, settings);
            await File.WriteAllTextAsync(fullPath, processedContent);
        }
    }

    private string ProcessTemplateContent(string content, Settings settings)
    {
        var packageName = string.IsNullOrEmpty(settings.PackageName)
            ? settings.Name
            : settings.PackageName;

        var description = string.IsNullOrEmpty(settings.Description)
            ? $"A modern .NET application created with Ghost CLI"
            : settings.Description;

        var author = string.IsNullOrEmpty(settings.Author)
            ? Environment.UserName
            : settings.Author;

        return content
            .Replace("${ProjectName}", settings.Name)
            .Replace("${PackageName}", packageName)
            .Replace("${Description}", description)
            .Replace("${Author}", author)
            .Replace("${Year}", DateTime.Now.Year.ToString())
            .Replace("${Date}", DateTime.Now.ToString("yyyy-MM-dd"));
    }

    private async Task InitializeGitRepository(string outputDir)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "init",
                    WorkingDirectory = outputDir,
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
                throw new Exception("Git initialization failed");
            }

            // Add .gitignore
            await File.WriteAllTextAsync(
                Path.Combine(outputDir, ".gitignore"),
                GetGitIgnoreContent());
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to initialize Git repository. Is Git installed?",
                ex,
                ErrorCode.ProcessError);
        }
    }

    private async Task SaveProjectMetadata(Settings settings, string outputDir)
    {
        var metadata = new ProjectMetadata
        {
            Name = settings.Name,
            Template = settings.Template,
            PackageName = settings.PackageName ?? settings.Name,
            Description = settings.Description,
            Author = settings.Author,
            CreatedAt = DateTime.UtcNow,
            Path = outputDir
        };

        await _dataApi.SetDataAsync($"projects:{settings.Name}", metadata);
    }

    private async Task ShowProjectStructure(string outputDir)
    {
        var root = new Tree($"[blue]{Path.GetFileName(outputDir)}/[/]");
        var files = Directory.GetFiles(outputDir, "*.*", SearchOption.AllDirectories);

        var filesByDirectory = files
            .OrderBy(f => f)
            .GroupBy(f => Path.GetDirectoryName(Path.GetRelativePath(outputDir, f)))
            .OrderBy(g => g.Key);

        foreach (var dirGroup in filesByDirectory)
        {
            IHasTreeNodes currentNode = root;

            if (!string.IsNullOrEmpty(dirGroup.Key))
            {
                var pathParts = dirGroup.Key.Split(Path.DirectorySeparatorChar);
                foreach (var part in pathParts)
                {
                    currentNode = currentNode.AddNode($"[blue]{part}/[/]");
                }
            }

            foreach (var file in dirGroup)
            {
                var fileName = Path.GetFileName(file);
                currentNode.AddNode($"[green]{fileName}[/]");
            }
        }

        AnsiConsole.Write(root);
    }

    private void ShowSuccessMessage(Settings settings, string outputDir)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            Align.Left(
                new Markup($@"Project created successfully!

To get started:
[grey]cd[/] {settings.Name}
[grey]dotnet run[/]

For more information, see the README.md file.
")))
            .Header("[green]Success![/]")
            .BorderColor(Color.Green));
    }

    private string GetGitIgnoreContent() => @"
## .NET Core
bin/
obj/
*.user

## Visual Studio
.vs/
*.suo
*.user
*.userosscache
*.sln.docstates

## Rider
.idea/

## Visual Studio Code
.vscode/

## User-specific files
*.suo
*.user
*.userosscache
*.sln.docstates

## Logs
*.log
logs/
";

    // Template creation methods
    private ProjectTemplate CreateDefaultTemplate() => new()
    {
        Name = "default",
        Description = "Basic console application template",
        Files = new Dictionary<string, string>
        {
            ["${ProjectName}.csproj"] = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PackageId>${PackageName}</PackageId>
    <Version>1.0.0</Version>
    <Authors>${Author}</Authors>
    <Description>${Description}</Description>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include=""Spectre.Console"" Version=""0.48.0"" />
  </ItemGroup>
</Project>",
            ["Program.cs"] = @"
using Spectre.Console;

namespace ${ProjectName};

public class Program
{
    public static async Task Main(string[] args)
    {
        AnsiConsole.Write(
            new FigletText(""${ProjectName}"")
                .Color(Color.Green));
                
        AnsiConsole.MarkupLine(""Welcome to [green]${ProjectName}[/]!"");
    }
}",
            ["README.md"] = @"
# ${ProjectName}

${Description}

## Getting Started

1. Build the project:
   ```bash
   dotnet build
   ```

2. Run the application:
   ```bash
   dotnet run
   ```

## License

This project is licensed under the MIT License.
"
        }
    };

    private ProjectTemplate CreateWebTemplate() => new()
    {
        Name = "web",
        Description = "ASP.NET Core web application template",
        Files = new Dictionary<string, string>
        {
            // Add web template files
        }
    };

    private ProjectTemplate CreateConsoleTemplate() => new()
    {
        Name = "console",
        Description = "Advanced console application template",
        Files = new Dictionary<string, string>
        {
            // Add console template files
        }
    };

    private ProjectTemplate CreateWorkerTemplate() => new()
    {
        Name = "worker",
        Description = "Background worker service template",
        Files = new Dictionary<string, string>
        {
            // Add worker template files
        }
    };
}

public class ProjectTemplate
{
    public string Name { get; set; }
    public string Description { get; set; }
    public Dictionary<string, string> Files { get; set; } = new();
}

public class ProjectMetadata
{
    public string Name { get; set; }
    public string Template { get; set; }
    public string PackageName { get; set; }
    public string Description { get; set; }
    public string Author { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Path { get; set; }
}