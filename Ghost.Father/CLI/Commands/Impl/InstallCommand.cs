using Ghost.Core.Config;
using Ghost.Core.Exceptions;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class InstallCommand : AsyncCommand<InstallCommand.Settings>
{

  private readonly string sdkVersion;
  public InstallCommand(GhostConfig config)
  {
    sdkVersion = "1.0.0"; //TODO: get sdkVersion - config.Core.Version;??
  }

  public class Settings : CommandSettings
  {
    [CommandOption("--force")]
    [Description("Force installation even if Ghost is already installed")]
    public bool Force { get; set; }

    [CommandOption("--path")]
    [Description("Custom installation path")]
    public string? CustomInstallPath { get; set; }
  }

  public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
  {
    // Store settings for use in other methods

    var installPath = settings.CustomInstallPath ?? GetDefaultInstallPath();

    return await AnsiConsole.Status()
        .StartAsync("Installing Ghost...", async ctx =>
        {
          try
          {
            if (Directory.Exists(installPath) && !settings.Force)
            {
              AnsiConsole.MarkupLine("[yellow]Ghost is already installed at:[/] " + installPath);
              AnsiConsole.MarkupLine("Use --force to reinstall.");
              return 1;
            }

            // Create installation directory
            ctx.Status("Creating installation directory...");
            Directory.CreateDirectory(installPath);

            // Create bin directory for executables
            var binDir = Path.Combine(installPath, "bin");
            Directory.CreateDirectory(binDir);

            // Create ghost apps directory
            var ghostAppsDir = Path.Combine(installPath, "ghosts");
            Directory.CreateDirectory(ghostAppsDir);

            // Copy executable and dependencies
            ctx.Status("Copying executable files...");
            var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (executablePath == null)
            {
              throw new GhostException("Could not determine executable path", ErrorCode.InstallationError);
            }

            var sourceDir = Path.GetDirectoryName(executablePath);
            if (sourceDir == null)
            {
              throw new GhostException("Could not determine source directory", ErrorCode.InstallationError);
            }

            // Copy all DLLs and dependencies to bin directory
            await CopyFilesAsync(sourceDir, binDir, ctx);

            // Copy and rename Ghost.Father.exe to ghost.exe
            var ghostExeName = OperatingSystem.IsWindows() ? "ghost.exe" : "ghost";
            var sourceExe = executablePath;
            var targetExe = Path.Combine(binDir, ghostExeName);

            ctx.Status($"Creating {ghostExeName}...");
            await CopyFileAsync(sourceExe, targetExe);

            // Make ghost executable on Unix systems
            if (!OperatingSystem.IsWindows())
            {
              try
              {
                var psi = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{targetExe}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var process = Process.Start(psi);
                if (process != null)
                {
                  await process.WaitForExitAsync();
                  if (process.ExitCode != 0)
                  {
                    G.LogWarn($"Failed to set executable permissions on {ghostExeName}");
                  }
                }
              }
              catch (Exception ex)
              {
                G.LogWarn($"Could not set executable permissions: {ex.Message}");
              }
            }

            // Copy templates
            ctx.Status("Installing templates...");

            var possibleTemplatePaths = new[]
            {
                Path.Combine(sourceDir, "Templates"), Path.Combine(sourceDir, "Template", "Templates"),
                Path.Combine(sourceDir, "..", "..", "..", "Templates"), Path.Combine(sourceDir, "..", "..", "..", "Template", "Templates"),
                Path.Combine(sourceDir, "..", "..", "..", "Ghost.Father", "Template", "Templates")
            };

            string sourceTemplatesPath = null;
            foreach (var path in possibleTemplatePaths)
            {
              if (Directory.Exists(path))
              {
                sourceTemplatesPath = path;
                G.LogInfo($"Found templates directory: {path}");
                break;
              }
            }

            if (sourceTemplatesPath == null)
            {
              G.LogWarn("Templates directory not found. Skipping template installation.");
            } else
            {
              // Create target templates directory
              var targetTemplatesPath = Path.Combine(installPath, "templates");
              Directory.CreateDirectory(targetTemplatesPath);

              // Get all template folders
              var templateFolders = Directory.GetDirectories(sourceTemplatesPath);
              G.LogInfo($"Found {templateFolders.Length} templates to install");

              // Copy each template folder
              foreach (var templateFolder in templateFolders)
              {
                var templateName = Path.GetFileName(templateFolder);
                ctx.Status($"Installing template: {templateName}...");

                var targetFolder = Path.Combine(targetTemplatesPath, templateName);

                // Check if template already exists
                bool shouldCopy = true;
                if (Directory.Exists(targetFolder) && !settings.Force)
                {
                  var replaceTemplate = AnsiConsole.Confirm(
                      $"Template '{templateName}' already exists. Replace it?", false);

                  if (!replaceTemplate)
                  {
                    ctx.Status($"Skipping existing template: {templateName}");
                    shouldCopy = false;
                  }
                }

                if (shouldCopy)
                {
                  // Delete existing template if it exists
                  if (Directory.Exists(targetFolder))
                  {
                    try
                    {
                      Directory.Delete(targetFolder, true);
                    }
                    catch (Exception ex)
                    {
                      G.LogWarn($"Failed to delete existing template: {ex.Message}");
                    }
                  }

                  // Copy the template folder
                  await CopyDirectoryAsync(templateFolder, targetFolder);
                  G.LogInfo($"Installed template: {templateName}");
                }
              }
            }


            // Add to PATH
            ctx.Status("Updating system PATH...");
            await UpdatePathAsync(binDir);

            // Create libs directory and build SDK
            ctx.Status("Building SDK libraries...");
            var libsDir = Path.Combine(installPath, "libs");
            Directory.CreateDirectory(libsDir);

            // Build SDK and Core DLLs
            if (!await BuildSdkLibrariesAsync(libsDir, ctx))
            {
              AnsiConsole.MarkupLine("[yellow]Warning: Failed to build SDK libraries.[/] " +
                                     "Projects will use NuGet packages instead.");
            }

            // Set environment variable for Ghost installation
            Environment.SetEnvironmentVariable("GHOST_INSTALL", installPath, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable("GHOST_INSTALL", installPath); // Current process

            AnsiConsole.MarkupLine("[green]Ghost installed successfully![/]");
            AnsiConsole.MarkupLine($"Installation path: {installPath}");
            AnsiConsole.MarkupLine($"Added to PATH: {binDir}");
            AnsiConsole.MarkupLine($"Ghost apps directory: {ghostAppsDir}");
            AnsiConsole.MarkupLine($"SDK libraries: {libsDir}");
            AnsiConsole.MarkupLine("\nRun [bold]ghost --help[/] to see available commands");
            return 0;
            // Modify this part in the ExecuteAsync method:
          }
          catch (Exception ex)
          {
            AnsiConsole.MarkupLine($"[red]Error during installation:[/] {ex.Message}");
            if (Directory.Exists(installPath) && settings.Force)
            {
              try
              {
                SafeDirectoryDelete(installPath);
                AnsiConsole.MarkupLine("[grey]Cleaned up installation directory[/]");
              }
              catch (Exception cleanupEx)
              {
                AnsiConsole.MarkupLine($"[grey]Could not clean up installation directory: {cleanupEx.Message}[/]");
              }
            }
            return 1;
          }
        });
  }

  private void SafeDirectoryDelete(string path)
  {
    try
    {
      // Try to release any potential file locks
      GC.Collect();
      GC.WaitForPendingFinalizers();

      // Try direct deletion first
      Directory.Delete(path, true);
    }
    catch (IOException)
    {
      // If failed due to locks, try alternative approach
      if (OperatingSystem.IsWindows())
      {
        // On Windows, use cmd to delete
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/C rd /S /Q \"{path}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi);
        process?.WaitForExit();
      } else
      {
        // On Linux/macOS, use rm command
        var psi = new ProcessStartInfo
        {
            FileName = "rm",
            Arguments = $"-rf \"{path}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(psi);
        process?.WaitForExit();
      }
    }
  }

  private async Task CopyFilesAsync(string sourceDir, string targetDir, StatusContext ctx)
  {
    // Create the target directory
    Directory.CreateDirectory(targetDir);

    // Copy all DLLs and dependent files
    foreach (var file in Directory.GetFiles(sourceDir))
    {
      var fileName = Path.GetFileName(file);

      // Skip files we don't want to copy
      if (fileName.Equals("Ghost.Father.exe", StringComparison.OrdinalIgnoreCase) ||
          fileName.Equals("Ghost.Father", StringComparison.OrdinalIgnoreCase))
      {
        // We'll copy this separately with a different name
        continue;
      }

      ctx.Status($"Copying {fileName}...");
      var targetPath = Path.Combine(targetDir, fileName);
      await CopyFileAsync(file, targetPath);
    }

    // Copy dependency directories (skip Templates, we'll handle that separately)
    foreach (var dir in Directory.GetDirectories(sourceDir))
    {
      var dirName = Path.GetFileName(dir);

      // Skip templates directory
      if (dirName.Equals("Templates", StringComparison.OrdinalIgnoreCase) ||
          dirName.Equals("templates", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      var targetPath = Path.Combine(targetDir, dirName);
      ctx.Status($"Copying directory: {dirName}...");
      await CopyDirectoryAsync(dir, targetPath);
    }
  }

  private async Task CopyDirectoryAsync(string sourceDir, string targetDir)
  {
    // Create the target directory
    Directory.CreateDirectory(targetDir);

    // Copy all files
    foreach (var file in Directory.GetFiles(sourceDir))
    {
      var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
      await CopyFileAsync(file, targetFile);
    }

    // Copy all subdirectories recursively
    foreach (var dir in Directory.GetDirectories(sourceDir))
    {
      var targetSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
      await CopyDirectoryAsync(dir, targetSubDir);
    }
  }

  private async Task CopyFileAsync(string sourcePath, string targetPath)
  {
    const int bufferSize = 81920; // 80 KB buffer
    const int maxRetries = 3;
    int retryCount = 0;
    bool success = false;

    while (!success && retryCount < maxRetries)
    {
      try
      {
        // Try to copy with file sharing to prevent locks
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite, // Allow reading while file is in use
            bufferSize,
            true);

        // Create temp file first, then move to target
        var tempFilePath = $"{targetPath}.tmp";

        await using (var targetStream = new FileStream(
            tempFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            true))
        {
          await sourceStream.CopyToAsync(targetStream);
        }

        // Check if target file is locked
        if (File.Exists(targetPath))
        {
          try
          {
            // Try to delete the existing file
            File.Delete(targetPath);
          }
          catch (IOException)
          {
            // If it's locked, try to replace it with the temp file
            if (OperatingSystem.IsWindows())
            {
              // On Windows, try using movefileex with replace existing flag
              MoveFileWithReplace(tempFilePath, targetPath);
            } else
            {
              // For other platforms, try a direct replacement
              File.Move(tempFilePath, targetPath, true);
            }
            success = true;
            continue;
          }
        }

        // Move temp file to target
        File.Move(tempFilePath, targetPath, true);
        success = true;
      }
      catch (IOException ex)
      {
        retryCount++;
        if (retryCount >= maxRetries)
        {
          // If all retries failed, try to use alternative copy method
          if (TryAlternativeCopy(sourcePath, targetPath))
          {
            success = true;
          } else
          {
            G.LogError(ex, $"Failed to copy file from {sourcePath} to {targetPath} after {maxRetries} attempts");
            throw;
          }
        } else
        {
          // Wait before retry
          await Task.Delay(1000 * retryCount);
          G.LogWarn($"Retrying file copy ({retryCount}/{maxRetries}): {Path.GetFileName(targetPath)}");
        }
      }
      catch (Exception ex)
      {
        G.LogError(ex, $"Failed to copy file from {sourcePath} to {targetPath}");
        throw;
      }
    }
  }

// Helper method for Windows to replace a file in use
  private static void MoveFileWithReplace(string sourcePath, string targetPath)
  {
    // For Windows, we can use P/Invoke to MoveFileEx if needed
    // But for now, try the simpler approach with File.Copy and File.Delete
    File.Copy(sourcePath, targetPath, true);
    try
    {
      File.Delete(sourcePath);
    }
    catch
    {
      // Ignore cleanup errors
    }
  }

// Alternative approach to copy files when standard methods fail
  private bool TryAlternativeCopy(string sourcePath, string targetPath)
  {
    try
    {
      // Try PowerShell or other command-line tools
      if (OperatingSystem.IsWindows())
      {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-Command \"Copy-Item -Path '{sourcePath}' -Destination '{targetPath}' -Force\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        process?.WaitForExit();
        return process?.ExitCode == 0;
      } else
      {
        // For Linux/macOS, try cp command
        var psi = new ProcessStartInfo
        {
            FileName = "cp",
            Arguments = $"-f \"{sourcePath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        process?.WaitForExit();
        return process?.ExitCode == 0;
      }
    }
    catch
    {
      return false;
    }
  }
  private async Task UpdatePathAsync(string binDir)
  {
    // Add the bin directory to the system PATH
    var envTarget = EnvironmentVariableTarget.User;
    var currentPath = Environment.GetEnvironmentVariable("PATH", envTarget) ?? "";

    if (!currentPath.Split(Path.PathSeparator).Contains(binDir, StringComparer.OrdinalIgnoreCase))
    {
      var newPath = currentPath + Path.PathSeparator + binDir;
      Environment.SetEnvironmentVariable("PATH", newPath, envTarget);

      // Also update current process PATH
      Environment.SetEnvironmentVariable("PATH", newPath);
    }

    await Task.CompletedTask;
  }

  /// <summary>
  /// Builds the SDK and Core libraries and places them in the libs directory
  /// </summary>
  private async Task<bool> BuildSdkLibrariesAsync(string libsDir, StatusContext ctx)
  {
    try
    {
      // Create temp directories for SDK and Core projects
      var tempDir = Path.Combine(Path.GetTempPath(), $"ghost-sdk-build-{Guid.NewGuid()}");
      Directory.CreateDirectory(tempDir);

      var coreDir = Path.Combine(tempDir, "Ghost.Core");
      var sdkDir = Path.Combine(tempDir, "Ghost.SDK");

      Directory.CreateDirectory(coreDir);
      Directory.CreateDirectory(sdkDir);

      try
      {
        // Create Ghost.Core project
        ctx.Status("Creating Ghost.Core project...");
        await CreateCoreProjectAsync(coreDir, sdkVersion);

        // Build Ghost.Core
        ctx.Status("Building Ghost.Core...");
        if (!await BuildProjectAsync(coreDir))
        {
          G.LogError("Failed to build Ghost.Core");
          return false;
        }

        // Create Ghost.SDK project
        ctx.Status("Creating Ghost.SDK project...");
        await CreateSdkProjectAsync(sdkDir, sdkVersion);

        // Build Ghost.SDK
        ctx.Status("Building Ghost.SDK...");
        if (!await BuildProjectAsync(sdkDir))
        {
          G.LogError("Failed to build Ghost.SDK");
          return false;
        }

        // Copy built DLLs to libs directory
        var coreDllPath = Path.Combine(coreDir, "bin", "Debug", "net8.0", "Ghost.Core.dll");
        var sdkDllPath = Path.Combine(sdkDir, "bin", "Debug", "net8.0", "Ghost.SDK.dll");

        if (File.Exists(coreDllPath) && File.Exists(sdkDllPath))
        {
          await CopyFileAsync(coreDllPath, Path.Combine(libsDir, "Ghost.Core.dll"));
          await CopyFileAsync(sdkDllPath, Path.Combine(libsDir, "Ghost.SDK.dll"));

          // Also copy any dependencies
          var coreDepDir = Path.GetDirectoryName(coreDllPath);
          var sdkDepDir = Path.GetDirectoryName(sdkDllPath);

          if (coreDepDir != null)
          {
            foreach (var file in Directory.GetFiles(coreDepDir))
            {
              if (!file.EndsWith(".dll") || Path.GetFileName(file) == "Ghost.Core.dll")
                continue;

              await CopyFileAsync(file, Path.Combine(libsDir, Path.GetFileName(file)));
            }
          }

          if (sdkDepDir != null)
          {
            foreach (var file in Directory.GetFiles(sdkDepDir))
            {
              if (!file.EndsWith(".dll") || Path.GetFileName(file) == "Ghost.SDK.dll" ||
                  Path.GetFileName(file) == "Ghost.Core.dll" ||
                  File.Exists(Path.Combine(libsDir, Path.GetFileName(file))))
                continue;

              await CopyFileAsync(file, Path.Combine(libsDir, Path.GetFileName(file)));
            }
          }

          return true;
        } else
        {
          G.LogError($"SDK libraries not found after build. Core: {File.Exists(coreDllPath)}, SDK: {File.Exists(sdkDllPath)}");
          return false;
        }
      }
      finally
      {
        // Clean up temp directory
        try
        {
          Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
          G.LogWarn($"Failed to clean up temp directory: {ex.Message}");
        }
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to build SDK libraries");
      return false;
    }
  }

  /// <summary>
  /// Creates the Ghost.Core project
  /// </summary>
  private async Task CreateCoreProjectAsync(string coreDir, string version)
  {
    // Create minimal Core implementation
    var coreCode = @"using System;

namespace Ghost.Core 
{
    public enum ErrorCode 
    { 
        Unknown, 
        ProcessError, 
        StorageError, 
        ConfigurationError, 
        NetworkError,
        TemplateError,
        TemplateNotFound,
        GitError,
        InstallationError
    }
    
    public class GhostException : Exception 
    {
        public ErrorCode Code { get; }
        
        public GhostException(string message) : base(message) 
        {
            Code = ErrorCode.Unknown;
        }
        
        public GhostException(string message, Exception innerException) 
            : base(message, innerException) 
        {
            Code = ErrorCode.Unknown;
        }
        
        public GhostException(string message, ErrorCode code) : base(message) 
        {
            Code = code;
        }
        
        public GhostException(string message, Exception innerException, ErrorCode code) 
            : base(message, innerException) 
        {
            Code = code;
        }
    }
}

namespace Ghost.Core.Logging
{
    public interface ILogger
    {
        void Log(string message, string level = ""INFO"", Exception ex = null);
    }
}";

    var coreCsproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>{version}</Version>
    <Authors>Ghost Team</Authors>
    <Description>Core library for Ghost applications</Description>
  </PropertyGroup>
</Project>";

    // Create directories
    Directory.CreateDirectory(Path.Combine(coreDir, "Logging"));

    // Write Core files
    await File.WriteAllTextAsync(Path.Combine(coreDir, "GhostCore.cs"), coreCode);
    await File.WriteAllTextAsync(Path.Combine(coreDir, "Ghost.Core.csproj"), coreCsproj);
  }

  /// <summary>
  /// Creates the Ghost.SDK project
  /// </summary>
  private async Task CreateSdkProjectAsync(string sdkDir, string version)
  {
    // Create minimal SDK implementation
    var sdkCode = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ghost 
{
    public static class G 
    {
        public static void LogInfo(string message) => Console.WriteLine($""[INFO] {message}"");
        public static void LogDebug(string message) => Console.WriteLine($""[DEBUG] {message}"");
        public static void LogWarn(string message) => Console.WriteLine($""[WARN] {message}"");
        public static void LogError(string message, Exception ex = null) 
        {
            Console.WriteLine($""[ERROR] {message}"");
            if (ex != null) Console.WriteLine(ex.ToString());
        }
        
        public static void LogInfo(string message, params object[] args)
        {
            LogInfo(string.Format(message, args));
        }

        public static void LogDebug(string message, params object[] args)
        {
            LogDebug(string.Format(message, args));
        }

        public static void LogWarn(string message, params object[] args)
        {
            LogWarn(string.Format(message, args));
        }

        public static void LogError(string message, params object[] args)
        {
            LogError(string.Format(message, args));
        }

        public static void LogError(Exception ex, string message, params object[] args)
        {
            LogError(string.Format(message, args), ex);
        }
    }
}

namespace Ghost.SDK 
{
    using Ghost.Core;
    
    public class GhostApp 
    {
        public GhostApp() 
        {
            Ghost.G.LogInfo(""Creating Ghost application"");
        }
        
        public virtual Task RunAsync(IEnumerable<string> args) 
        {
            Ghost.G.LogInfo(""Hello from Ghost SDK!"");
            return Task.CompletedTask;
        }
        
        public virtual Task ExecuteAsync(IEnumerable<string> args) 
        {
            return RunAsync(args);
        }
    }
}";

    var sdkCsproj = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>{version}</Version>
    <Authors>Ghost Team</Authors>
    <Description>SDK for building Ghost applications</Description>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\Ghost.Core\Ghost.Core.csproj"" />
  </ItemGroup>
</Project>";

    // Write SDK files
    await File.WriteAllTextAsync(Path.Combine(sdkDir, "GhostSDK.cs"), sdkCode);
    await File.WriteAllTextAsync(Path.Combine(sdkDir, "Ghost.SDK.csproj"), sdkCsproj);
  }

  /// <summary>
  /// Builds a project
  /// </summary>
  private async Task<bool> BuildProjectAsync(string projectDir)
  {
    try
    {
      var psi = new ProcessStartInfo
      {
          FileName = "dotnet",
          Arguments = "build",
          WorkingDirectory = projectDir,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
      };

      var process = Process.Start(psi);
      if (process == null)
      {
        G.LogError($"Failed to start dotnet build process for {projectDir}");
        return false;
      }

      var output = await process.StandardOutput.ReadToEndAsync();
      var error = await process.StandardError.ReadToEndAsync();
      await process.WaitForExitAsync();

      if (process.ExitCode != 0)
      {
        G.LogError($"Build failed for {projectDir}: {error}");
        return false;
      }

      return true;
    }
    catch (Exception ex)
    {
      G.LogError(ex, $"Error building project in {projectDir}");
      return false;
    }
  }

  private string GetDefaultInstallPath()
  {
    if (OperatingSystem.IsWindows())
    {
      // Windows: Use LocalApplicationData
      var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      return Path.Combine(localAppData, "Ghost");
    } else if (OperatingSystem.IsMacOS())
    {
      // macOS: Use user's Library folder
      var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      return Path.Combine(homeDir, "Library", "Application Support", "Ghost");
    } else
    {
      // Linux: Use standard ~/.local/share
      var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      return Path.Combine(homeDir, ".local", "share", "Ghost");
    }
  }

  /// <summary>
  /// Creates a template for a .ghost.yaml file inside the templates directory
  /// </summary>
  private async Task UpdateTemplateForLocalReferencesAsync(string templatesDir)
  {
    try
    {
      // Find all .ghost.yaml.tpl files in the templates directory and subdirectories
      var templateFiles = Directory.GetFiles(templatesDir, ".ghost.yaml.tpl", SearchOption.AllDirectories);

      foreach (var file in templateFiles)
      {
        var content = await File.ReadAllTextAsync(file);

        // Update template to point to local references
        // This may vary based on your specific template format

        await File.WriteAllTextAsync(file, content);
      }

      // Update project template files to use local references
      var csprojTemplates = Directory.GetFiles(templatesDir, "*.csproj.tpl", SearchOption.AllDirectories);

      foreach (var file in csprojTemplates)
      {
        var content = await File.ReadAllTextAsync(file);

        // Replace NuGet package references with local references
        var packageRefPattern = @"<PackageReference\s+Include=""Ghost\.SDK""\s+Version=""[^""]*""\s*/>";
        var localRefReplacement = @"<Reference Include=""Ghost.SDK"">
      <HintPath>$(GhostInstallDir)\libs\Ghost.SDK.dll</HintPath>
    </Reference>
    <Reference Include=""Ghost.Core"">
      <HintPath>$(GhostInstallDir)\libs\Ghost.Core.dll</HintPath>
    </Reference>";

        if (content.Contains("<PackageReference Include=\"Ghost.SDK\""))
        {
          content = System.Text.RegularExpressions.Regex.Replace(content, packageRefPattern, localRefReplacement);

          // Add property group for Ghost install path
          if (!content.Contains("GhostInstallDir"))
          {
            content = content.Replace("<PropertyGroup>", "<PropertyGroup>\n    <GhostInstallDir Condition=\"'$(GhostInstallDir)' == ''\">$(GHOST_INSTALL)</GhostInstallDir>");
          }

          await File.WriteAllTextAsync(file, content);
        }
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to update templates for local references");
    }
  }
}
