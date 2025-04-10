using Ghost.Core.Exceptions;
using Ghost.Templates;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class CreateCommand : AsyncCommand<CreateCommand.Settings>
{
  private readonly TemplateManager _templateManager;
  private const string GHOSTS_FOLDER_NAME = "ghosts";

  public CreateCommand()
  {
    try
    {
      // Get base templates path from executable location
      var templatesPath = Path.Combine(
          AppContext.BaseDirectory, "../templates");

      // Create template manager (this will initialize default templates if needed)
      _templateManager = new TemplateManager(templatesPath);

      // Log available templates
      var templates = _templateManager.GetAvailableTemplates();
      G.LogDebug($"Loaded {templates.Count} templates:");
      foreach (var template in templates.Values)
      {
        G.LogDebug($"- {template.Name}: {template.Description}");
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to initialize template manager");
      throw new GhostException(
          "Failed to initialize templates. Please reinstall Ghost.",
          ex,
          ErrorCode.TemplateError);
    }
  }

  public class Settings : CommandSettings
  {
    [CommandArgument(0, "[name]")]
    [Description("Name of the new Ghost app")]
    public string Name { get; set; }

    [CommandOption("--template")]
    [Description("Template to use (default: full)")]
    public string Template { get; set; } = "full";

    [CommandOption("--description")]
    [Description("Description of the app")]
    public string Description { get; set; }

    [CommandOption("--namespace")]
    [Description("Root namespace for the app")]
    public string Namespace { get; set; }

    [CommandOption("--use-nuget")]
    [Description("Use NuGet packages instead of local references")]
    public bool UseNuget { get; set; }

    [CommandOption("--sdk-version")]
    [Description("SDK version to use")]
    public string SdkVersion { get; set; } = "1.0.0";
  }

  public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
  {
    try
    {
      // Determine Ghost installation directory
      var ghostInstallDir = Environment.GetEnvironmentVariable("GHOST_INSTALL");
      var hasLocalLibs = false;

      if (!string.IsNullOrEmpty(ghostInstallDir))
      {
        var libsPath = Path.Combine(ghostInstallDir, "libs");
        if (Directory.Exists(libsPath))
        {
          var sdkFile = Path.Combine(libsPath, "Ghost.SDK.dll");
          var coreFile = Path.Combine(libsPath, "Ghost.Core.dll");

          hasLocalLibs = File.Exists(sdkFile) && File.Exists(coreFile);

          if (hasLocalLibs)
          {
            G.LogInfo($"Using local SDK from: {libsPath}");
          } else if (!settings.UseNuget)
          {
            G.LogWarn($"Local SDK not found in {libsPath}. Will use NuGet packages.");
            settings.UseNuget = true;
          }
        } else if (!settings.UseNuget)
        {
          G.LogWarn($"Libs directory not found: {libsPath}. Will use NuGet packages.");
          settings.UseNuget = true;
        }
      } else if (!settings.UseNuget)
      {
        G.LogWarn("GHOST_INSTALL environment variable not set. Will use NuGet packages.");
        settings.UseNuget = true;
      }

      // Create the "ghost" folder in the proper location
      string outputPath;
      if (!string.IsNullOrEmpty(ghostInstallDir))
      {
        outputPath = Path.Combine(ghostInstallDir, GHOSTS_FOLDER_NAME);
      } else
      {
        // Fall back to GHOSTS_FOLDER_NAME folder in the current directory
        outputPath = Path.Combine(AppContext.BaseDirectory, GHOSTS_FOLDER_NAME);
      }

      Directory.CreateDirectory(outputPath);

      // Validate template exists
      var templates = _templateManager.GetAvailableTemplates();
      if (!templates.ContainsKey(settings.Template))
      {
        AnsiConsole.MarkupLine($"[red]Error:[/] Template '{settings.Template}' not found");
        AnsiConsole.MarkupLine("\nAvailable templates:");
        foreach (var temp in templates)
        {
          AnsiConsole.MarkupLine($" [grey]{temp.Key}[/] - {temp.Value.Description}");
        }
        return 1;
      }

      // Add extra template variables for SDK paths
      var templateVars = new Dictionary<string, string>
      {
          ["namespace"] = "Ghosttt",
          ["ghost_install_dir"] = ghostInstallDir ?? "",
          ["sdk_version"] = settings.SdkVersion,
          ["use_local_libs"] = (!settings.UseNuget && hasLocalLibs).ToString().ToLower()
      };

      // Create project from template
      AnsiConsole.MarkupLine($"Creating project [green]{settings.Name}[/] from template '[blue]{settings.Template}[/]'...");

      var projectDir = await _templateManager.CreateFromTemplateAsync(
          settings.Template, settings.Name, outputPath, templateVars);

      // Initialize git repository if successful
      if (projectDir.Exists)
      {
        try
        {
          if (!Directory.Exists(Path.Combine(projectDir.FullName, ".git")))
          {
            // Check if git is available
            bool gitAvailable = await IsGitAvailableAsync();
            if (gitAvailable)
            {
              await RunGitCommandAsync("init", projectDir.FullName);
              AnsiConsole.MarkupLine("[grey]Initialized Git repository[/]");

              // Create a basic .gitignore file
              await CreateGitignoreFileAsync(projectDir.FullName);
            }
          }
        }
        catch (Exception ex)
        {
          AnsiConsole.MarkupLine($"[yellow]Warning:[/] Failed to initialize Git repository: {ex.Message}");
        }

        // Perform a package restore if needed
        if (settings.UseNuget)
        {
          AnsiConsole.MarkupLine("Restoring NuGet packages...");
          await RestorePackagesAsync(projectDir.FullName);
        }
      }

      AnsiConsole.MarkupLine($"[green]Created new Ghost app:[/] {settings.Name} at {projectDir.FullName}");
      AnsiConsole.MarkupLine("");
      AnsiConsole.MarkupLine("To get started:");

      // Show path based on installation directory
      if (!string.IsNullOrEmpty(ghostInstallDir))
      {
        AnsiConsole.MarkupLine($"[grey]cd[/] {Path.Combine(ghostInstallDir, GHOSTS_FOLDER_NAME, projectDir.Name)}");
      } else
      {
        AnsiConsole.MarkupLine($"[grey]cd[/] {Path.Combine(GHOSTS_FOLDER_NAME, projectDir.Name)}");
      }

      AnsiConsole.MarkupLine("[grey]dotnet build[/]");
      AnsiConsole.MarkupLine($"[grey]ghost run[/] {settings.Name}");

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
      G.LogError(ex, "Unexpected error during project creation");
      return 1;
    }
  }

  private async Task CreateGitignoreFileAsync(string projectDir)
  {
    var content = @"## .NET Core
bin/
obj/
*.user

## IDE
.vs/
.vscode/
.idea/

## Ghost
logs/
cache/
data/
outputs/

## OS
.DS_Store
Thumbs.db
";

    await File.WriteAllTextAsync(Path.Combine(projectDir, ".gitignore"), content);
  }

  private async Task<bool> RestorePackagesAsync(string projectDir)
  {
    try
    {
      var psi = new ProcessStartInfo
      {
          FileName = "dotnet",
          Arguments = "restore",
          WorkingDirectory = projectDir,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
      };

      var process = Process.Start(psi);
      if (process == null)
      {
        G.LogError("Failed to start dotnet restore process");
        return false;
      }

      await process.WaitForExitAsync();
      return process.ExitCode == 0;
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to restore packages");
      return false;
    }
  }

  private async Task<bool> IsGitAvailableAsync()
  {
    try
    {
      var psi = new ProcessStartInfo
      {
          FileName = "git",
          Arguments = "--version",
          RedirectStandardOutput = true,
          UseShellExecute = false,
          CreateNoWindow = true
      };

      var process = Process.Start(psi);
      if (process == null) return false;

      await process.WaitForExitAsync();
      return process.ExitCode == 0;
    }
    catch
    {
      return false;
    }
  }

  private async Task RunGitCommandAsync(string command, string workingDirectory)
  {
    var psi = new ProcessStartInfo
    {
        FileName = "git",
        Arguments = command,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    var process = Process.Start(psi);
    if (process == null) throw new InvalidOperationException("Failed to start git process");

    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
    {
      var error = await process.StandardError.ReadToEndAsync();
      throw new InvalidOperationException($"Git command failed: {error}");
    }
  }
}
