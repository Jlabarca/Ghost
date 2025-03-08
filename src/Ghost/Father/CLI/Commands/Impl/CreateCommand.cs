using Ghost.Core;
using Ghost.Templates;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Ghost.Father.CLI.Commands;

public class CreateCommand : AsyncCommand<CreateCommand.Settings>
{
    private readonly TemplateManager _templateManager;

    public CreateCommand()
    {
        try
        {
            // Get base templates path from executable location
            var templatesPath = Path.Combine(
                AppContext.BaseDirectory, "templates");

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
        [Description("Template to use (default: lean)")]
        public string Template { get; set; } = "lean";

        [CommandOption("--description")]
        [Description("Description of the app")]
        public string Description { get; set; }

        [CommandOption("--namespace")]
        [Description("Root namespace for the app")]
        public string Namespace { get; set; }

        [CommandOption("--skip-sdk")]
        [Description("Skip SDK dependency setup")]
        public bool SkipSdk { get; set; }

        [CommandOption("--sdk-version")]
        [Description("SDK version to use")]
        public string SdkVersion { get; set; } = "1.0.0";
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Create the "ghosts" folder in the directory where ghost.exe is running
            var ghostsPath = Path.Combine(AppContext.BaseDirectory, "ghosts");
            Directory.CreateDirectory(ghostsPath);
            var outputPath = ghostsPath;

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

            // Validate template environment
            var template = templates[settings.Template];
            if (!await template.ValidateEnvironmentAsync())
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Template requirements not met:");
                foreach (var package in template.RequiredPackages)
                {
                    AnsiConsole.MarkupLine($" [grey]{package.Key}[/] v{package.Value}");
                }
                return 1;
            }

            // Create project from template
            G.LogInfo($"Creating project from template '{settings.Template}' in {outputPath}...");
            var projectDir = await _templateManager.CreateFromTemplateAsync(
                settings.Template, settings.Name, outputPath);

            // Set up NuGet configuration
            bool nugetSetupSuccessful = true;
            if (projectDir.Exists && !settings.SkipSdk)
            {
                nugetSetupSuccessful = await EnsureSdkAvailableAsync(projectDir.FullName, settings.SdkVersion);

                // If NuGet setup failed but project was created, we should warn the user
                if (!nugetSetupSuccessful)
                {
                    AnsiConsole.MarkupLine("[yellow]Warning:[/] NuGet configuration failed. " +
                        "You may need to manually configure NuGet packages.");
                }
            }

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
                        }
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] Failed to initialize Git repository: {ex.Message}");
                }
            }

            AnsiConsole.MarkupLine($"[green]Created new Ghost app:[/] {settings.Name} at {projectDir.FullName}");
            AnsiConsole.MarkupLine("");
            AnsiConsole.MarkupLine("To get started:");
            AnsiConsole.MarkupLine($"[grey]cd[/] {Path.Combine("ghosts", projectDir.Name)}");

            if (!nugetSetupSuccessful)
            {
                AnsiConsole.MarkupLine("[grey]dotnet add package Ghost.SDK --version 1.0.0[/]");
            }

            AnsiConsole.MarkupLine("[grey]dotnet restore[/]");
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

    /// <summary>
    /// Ensures the Ghost SDK is available for the project by setting up a local NuGet feed
    /// and copying the SDK packages to it if needed.
    /// </summary>
    private async Task<bool> EnsureSdkAvailableAsync(string projectDir, string sdkVersion)
    {
        try
        {
            // Don't use AnsiConsole.Status here as it might conflict with other dynamic displays
            G.LogInfo("Ensuring SDK is available...");

            // Skip SDK dependency setup and just add NuGet.config with nuget.org source
            G.LogInfo("Configuring NuGet sources...");
            var nugetConfigPath = Path.Combine(projectDir, "NuGet.config");
            var nugetConfig = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <packageSources>
    <add key=""nuget.org"" value=""https://api.nuget.org/v3/index.json"" protocolVersion=""3"" />
  </packageSources>
</configuration>";
            await File.WriteAllTextAsync(nugetConfigPath, nugetConfig);

            // Update project to reference packages from nuget.org
            await UpdateProjectReferencesAsync(projectDir, sdkVersion);

            // Restore packages
            G.LogInfo("Restoring NuGet packages...");
            var restoreSuccess = await RestorePackagesAsync(projectDir);

            return restoreSuccess;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to configure NuGet");
            return false;
        }
    }

    private async Task<bool> BuildAndPackProjectAsync(string projectDir, string packageId, string version, string outputDir)
    {
        try
        {
            // Ensure the output directory exists
            Directory.CreateDirectory(outputDir);

            // First try to run dotnet pack
            var packArgs = $"pack -p:Version={version} -c Release -o \"{outputDir}\"";

            var packProcess = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = packArgs,
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(packProcess);
            if (process == null)
            {
                G.LogError($"Failed to start dotnet pack process for {packageId}");
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                G.LogError($"Failed to pack {packageId}: {error}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            G.LogError(ex, $"Error building and packing {packageId}");
            return false;
        }
    }

    private async Task<bool> CreateSimplifiedPackagesAsync(string outputDir, string version)
    {
        try
        {
            // Create minimal implementations for both packages
            var coreDir = Path.Combine(outputDir, "Ghost.Core");
            var sdkDir = Path.Combine(outputDir, "Ghost.SDK");

            Directory.CreateDirectory(coreDir);
            Directory.CreateDirectory(sdkDir);

            // Create Ghost.Core minimal implementation
            var coreCode = @"namespace Ghost.Core {
    public enum ErrorCode { 
        Unknown, ProcessError, StorageError, ConfigurationError, NetworkError 
    }
    
    public class GhostException : System.Exception {
        public ErrorCode Code { get; }
        
        public GhostException(string message) : base(message) {
            Code = ErrorCode.Unknown;
        }
        
        public GhostException(string message, System.Exception innerException) 
            : base(message, innerException) {
            Code = ErrorCode.Unknown;
        }
        
        public GhostException(string message, ErrorCode code) : base(message) {
            Code = code;
        }
        
        public GhostException(string message, System.Exception innerException, ErrorCode code) 
            : base(message, innerException) {
            Code = code;
        }
    }
}";
            var coreCsproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>{version}</Version>
    <Authors>Ghost Team</Authors>
    <Description>Core library for Ghost applications</Description>
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
  </PropertyGroup>
</Project>";

            // Write Core files
            await File.WriteAllTextAsync(Path.Combine(coreDir, "GhostCore.cs"), coreCode);
            await File.WriteAllTextAsync(Path.Combine(coreDir, "Ghost.Core.csproj"), coreCsproj);

            // Create Ghost.SDK minimal implementation
            var sdkCode = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ghost {
    public static class G {
        public static void LogInfo(string message) => Console.WriteLine($""[INFO] {message}"");
        public static void LogDebug(string message) => Console.WriteLine($""[DEBUG] {message}"");
        public static void LogWarn(string message) => Console.WriteLine($""[WARN] {message}"");
        public static void LogError(string message, Exception ex = null) {
            Console.WriteLine($""[ERROR] {message}"");
            if (ex != null) Console.WriteLine(ex.ToString());
        }
    }
}

namespace Ghost.SDK {
    public class GhostApp {
        public GhostApp() {
            Ghost.G.LogInfo(""Creating Ghost application"");
        }
        
        public virtual Task RunAsync(IEnumerable<string> args) {
            Ghost.G.LogInfo(""Hello from Ghost SDK!"");
            return Task.CompletedTask;
        }
        
        public virtual Task ExecuteAsync(IEnumerable<string> args) {
            return RunAsync(args);
        }
    }
}";
            var sdkCsproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>{version}</Version>
    <Authors>Ghost Team</Authors>
    <Description>SDK for building Ghost applications</Description>
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""Ghost.Core"" Version=""{version}"" />
  </ItemGroup>
</Project>";

            // Write SDK files
            await File.WriteAllTextAsync(Path.Combine(sdkDir, "GhostSDK.cs"), sdkCode);
            await File.WriteAllTextAsync(Path.Combine(sdkDir, "Ghost.SDK.csproj"), sdkCsproj);

            // Pack core package
            var coreSuccess = await BuildAndPackProjectAsync(coreDir, "Ghost.Core", version, outputDir);
            if (!coreSuccess) return false;

            // Pack SDK package
            var sdkSuccess = await BuildAndPackProjectAsync(sdkDir, "Ghost.SDK", version, outputDir);
            return sdkSuccess;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to create simplified packages");
            return false;
        }
    }

    private async Task UpdateProjectReferencesAsync(string projectDir, string sdkVersion)
    {
        // Find all project files
        foreach (var projFile in Directory.GetFiles(projectDir, "*.csproj"))
        {
            var content = await File.ReadAllTextAsync(projFile);

            // Update Ghost.SDK reference
            var sdkPattern = @"<PackageReference\s+Include=""Ghost\.SDK""\s+Version=""[^""]*""\s*/>";
            var sdkReplacement = $@"<PackageReference Include=""Ghost.SDK"" Version=""{sdkVersion}"" />";
            content = System.Text.RegularExpressions.Regex.Replace(content, sdkPattern, sdkReplacement);

            // Update Ghost.Core reference if present
            var corePattern = @"<PackageReference\s+Include=""Ghost\.Core""\s+Version=""[^""]*""\s*/>";
            var coreReplacement = $@"<PackageReference Include=""Ghost.Core"" Version=""{sdkVersion}"" />";
            content = System.Text.RegularExpressions.Regex.Replace(content, corePattern, coreReplacement);

            await File.WriteAllTextAsync(projFile, content);
        }
    }

    private async Task<bool> RestorePackagesAsync(string projectDir)
    {
        try
        {
            G.LogInfo($"Running 'dotnet restore' in {projectDir}");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "restore --verbosity normal",
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

            // Capture outputs
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Log full output for debugging
            G.LogDebug($"dotnet restore output:\n{output}");

            if (process.ExitCode != 0)
            {
                // Log the complete error message
                G.LogError($"dotnet restore failed with exit code {process.ExitCode}:\n{error}\n{output}");
                return false;
            }

            G.LogInfo("Package restore completed successfully");
            return true;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to restore packages");
            return false;
        }
    }

    private async Task<bool> IsDotNetAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
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