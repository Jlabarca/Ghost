using Ghost.Core.Config;
using Ghost.Core.Exceptions;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Ghost.Father.CLI.Commands;

public class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
  private readonly string sdkVersion;

  public InstallCommand(GhostConfig config)
  {
    sdkVersion = config.App?.Version ?? "1.0.0";
  }

  public class Settings : CommandSettings
  {
    [CommandOption("--force")]
    [Description("Force installation even if Ghost is already installed")]
    public bool Force { get; set; }

    [CommandOption("--path")]
    [Description("Custom installation path")]
    public string? CustomInstallPath { get; set; }

    [CommandOption("--repair")]
    [Description("Repair existing installation")]
    public bool Repair { get; set; }
  }

  private Settings _settings;

  public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
  {
    _settings = settings;
    var installPath = settings.CustomInstallPath ?? GetDefaultInstallPath();

    return await AnsiConsole.Status()
        .StartAsync("Installing Ghost...", async ctx =>
        {
          try
          {
            await TerminateGhostProcessesAsync();

            if (Directory.Exists(installPath) && !settings.Force && !settings.Repair)
            {
              AnsiConsole.MarkupLine("[yellow]Ghost is already installed at:[/] " + installPath);
              AnsiConsole.MarkupLine("Use --force to reinstall or --repair to repair the installation.");
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
                      $"Template '{templateName}' already exists. Replace it?",
                      false);
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

  /// <summary>
  /// Builds the SDK and Core libraries and places them in the libs directory.
  /// This uses the actual projects rather than creating mock implementations.
  /// Also copies all dependencies to ensure reference projects work properly.
  /// </summary>
  private async Task<bool> BuildSdkLibrariesAsync(string libsDir, StatusContext ctx)
  {
    try
    {
      // Find the actual project files relative to the executing assembly
      var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
      if (executablePath == null)
      {
        G.LogError("Failed to determine executable path");
        return false;
      }

      var sourceDir = Path.GetDirectoryName(executablePath);
      if (sourceDir == null)
      {
        G.LogError("Failed to determine source directory");
        return false;
      }

      // Look for the solution directory (navigate up if needed)
      ctx.Status("Locating project files...");
      var solutionDir = FindSolutionDirectory(sourceDir);
      if (solutionDir == null)
      {
        G.LogError("Could not find solution directory");
        return false;
      }

      G.LogInfo($"Found solution directory: {solutionDir}");

      // Find project paths
      var coreProjPath = Path.Combine(solutionDir, "Ghost.Core", "Ghost.Core.csproj");
      var sdkProjPath = Path.Combine(solutionDir, "Ghost.SDK", "Ghost.SDK.csproj");

      if (!File.Exists(coreProjPath))
      {
        G.LogError($"Ghost.Core project not found at: {coreProjPath}");
        return false;
      }

      if (!File.Exists(sdkProjPath))
      {
        G.LogError($"Ghost.SDK project not found at: {sdkProjPath}");
        return false;
      }

      G.LogInfo($"Found project files: {coreProjPath} and {sdkProjPath}");

      // Build Ghost.Core
      ctx.Status("Building Ghost.Core...");
      if (!await BuildProjectAsync(Path.GetDirectoryName(coreProjPath), "Ghost.Core.csproj"))
      {
        G.LogError("Failed to build Ghost.Core");
        return false;
      }

      // Build Ghost.SDK (depends on Ghost.Core)
      ctx.Status("Building Ghost.SDK...");
      if (!await BuildProjectAsync(Path.GetDirectoryName(sdkProjPath), "Ghost.SDK.csproj"))
      {
        G.LogError("Failed to build Ghost.SDK");
        return false;
      }

      // Copy built DLLs to libs directory
      ctx.Status("Copying built libraries to libs directory...");

      // Find the bin directories with the built DLLs
      var coreBinDir = Path.Combine(Path.GetDirectoryName(coreProjPath), "bin");
      var sdkBinDir = Path.Combine(Path.GetDirectoryName(sdkProjPath), "bin");

      // Find the DLLs - check both Debug and Release configurations
      string coreDllPath = null;
      string sdkDllPath = null;

      // Look for the newest DLL in either Debug or Release folders
      foreach (var config in new[]
      {
          "Release", "Debug"
      })
      {
        var corePath = FindDll(coreBinDir, config, "Ghost.Core.dll");
        var sdkPath = FindDll(sdkBinDir, config, "Ghost.SDK.dll");

        if (corePath != null && coreDllPath == null)
          coreDllPath = corePath;

        if (sdkPath != null && sdkDllPath == null)
          sdkDllPath = sdkPath;

        if (coreDllPath != null && sdkDllPath != null)
          break;
      }

      if (coreDllPath == null || !File.Exists(coreDllPath))
      {
        G.LogError("Ghost.Core.dll not found after build");
        return false;
      }

      if (sdkDllPath == null || !File.Exists(sdkDllPath))
      {
        G.LogError("Ghost.SDK.dll not found after build");
        return false;
      }

      G.LogInfo($"Found built DLLs: {coreDllPath} and {sdkDllPath}");

      // Create dependency analysis file to help troubleshoot dependency issues
      var depsOutputFile = Path.Combine(libsDir, "dependencies.txt");
      var depsWriter = new StreamWriter(depsOutputFile);
      await depsWriter.WriteLineAsync($"Ghost.Core.dll: {coreDllPath}");
      await depsWriter.WriteLineAsync($"Ghost.SDK.dll: {sdkDllPath}");
      await depsWriter.WriteLineAsync("Dependencies:");

      // Copy the DLLs to the libs directory
      await CopyFileAsync(coreDllPath, Path.Combine(libsDir, "Ghost.Core.dll"));
      await CopyFileAsync(sdkDllPath, Path.Combine(libsDir, "Ghost.SDK.dll"));

      // Copy dependencies
      var coreDllDir = Path.GetDirectoryName(coreDllPath);
      var sdkDllDir = Path.GetDirectoryName(sdkDllPath);

      // Create a HashSet to track copied files to avoid duplicates
      var copiedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      copiedFiles.Add("Ghost.Core.dll");
      copiedFiles.Add("Ghost.SDK.dll");

      // Copy all DLLs from Ghost.Core
      if (coreDllDir != null)
      {
        await depsWriter.WriteLineAsync($"\nCore dependencies from: {coreDllDir}");
        foreach (var file in Directory.GetFiles(coreDllDir, "*.dll"))
        {
          var fileName = Path.GetFileName(file);
          if (!copiedFiles.Contains(fileName))
          {
            await depsWriter.WriteLineAsync($"  {fileName}");
            var targetPath = Path.Combine(libsDir, fileName);
            await CopyFileAsync(file, targetPath);
            copiedFiles.Add(fileName);
          }
        }
      }

      // Copy all DLLs from Ghost.SDK
      if (sdkDllDir != null)
      {
        await depsWriter.WriteLineAsync($"\nSDK dependencies from: {sdkDllDir}");
        foreach (var file in Directory.GetFiles(sdkDllDir, "*.dll"))
        {
          var fileName = Path.GetFileName(file);
          if (!copiedFiles.Contains(fileName))
          {
            await depsWriter.WriteLineAsync($"  {fileName}");
            var targetPath = Path.Combine(libsDir, fileName);
            await CopyFileAsync(file, targetPath);
            copiedFiles.Add(fileName);
          }
        }
      }

      // Also check for dependencies in runtimes directories
      if (coreDllDir != null)
      {
        var runtimesDir = Path.Combine(Path.GetDirectoryName(coreDllDir), "runtimes");
        if (Directory.Exists(runtimesDir))
        {
          await depsWriter.WriteLineAsync($"\nRuntime dependencies for Core:");
          var targetRuntimesDir = Path.Combine(libsDir, "runtimes");
          Directory.CreateDirectory(targetRuntimesDir);

          // Copy all runtime-specific DLLs
          foreach (var runtimeDir in Directory.GetDirectories(runtimesDir))
          {
            var runtimeName = Path.GetFileName(runtimeDir);
            await depsWriter.WriteLineAsync($"  {runtimeName}/");

            // Create runtime directory in target
            var targetRuntimeDir = Path.Combine(targetRuntimesDir, runtimeName);
            Directory.CreateDirectory(targetRuntimeDir);

            // Copy all files in the runtime directory
            foreach (var dir in Directory.GetDirectories(runtimeDir))
            {
              var dirName = Path.GetFileName(dir);
              var targetSubDir = Path.Combine(targetRuntimeDir, dirName);
              Directory.CreateDirectory(targetSubDir);

              foreach (var file in Directory.GetFiles(dir, "*.dll"))
              {
                var fileName = Path.GetFileName(file);
                await depsWriter.WriteLineAsync($"    {dirName}/{fileName}");
                var targetPath = Path.Combine(targetSubDir, fileName);
                await CopyFileAsync(file, targetPath);
              }
            }
          }
        }
      }

      // Generate a list of all MS Extensions packages to help with project dependencies
      var msExtensions = copiedFiles
          .Where(f => f.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase))
          .OrderBy(f => f)
          .ToList();

      if (msExtensions.Count > 0)
      {
        await depsWriter.WriteLineAsync("\nMicrosoft.Extensions dependencies found:");
        foreach (var dll in msExtensions)
        {
          await depsWriter.WriteLineAsync($"  {dll}");
        }
      }

      // Close the dependencies writer
      depsWriter.Close();

      G.LogInfo($"Successfully built and copied SDK libraries to {libsDir}");
      G.LogInfo($"Dependency information written to {depsOutputFile}");
      return true;
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to build SDK libraries");
      return false;
    }
  }
  /// <summary>
  /// Finds a DLL in the specified bin directory and configuration
  /// </summary>
  private string FindDll(string binDir, string configuration, string dllName)
  {
    try
    {
      // Look in all target framework directories (net9.0, netstandard2.0, etc.)
      var configDir = Path.Combine(binDir, configuration);
      if (!Directory.Exists(configDir))
        return null;

      // Get all directories in the config folder (usually framework versions)
      foreach (var frameworkDir in Directory.GetDirectories(configDir))
      {
        var dllPath = Path.Combine(frameworkDir, dllName);
        if (File.Exists(dllPath))
          return dllPath;
      }

      // Also check directly in the config folder
      var directPath = Path.Combine(configDir, dllName);
      if (File.Exists(directPath))
        return directPath;

      return null;
    }
    catch (Exception ex)
    {
      G.LogError(ex, $"Error finding DLL {dllName} in {binDir}");
      return null;
    }
  }

  /// <summary>
  /// Finds the solution directory by searching up from the given directory
  /// </summary>
  private string FindSolutionDirectory(string startDir)
  {
    var currentDir = startDir;

    // Look up the directory tree
    while (currentDir != null)
    {
      // Look for sln files
      if (Directory.GetFiles(currentDir, "*.sln").Any())
      {
        return currentDir;
      }

      // Look for specific project directories that would indicate we're in the right place
      if (Directory.Exists(Path.Combine(currentDir, "Ghost.Core")) &&
          Directory.Exists(Path.Combine(currentDir, "Ghost.SDK")))
      {
        return currentDir;
      }

      // Move up one directory
      currentDir = Directory.GetParent(currentDir)?.FullName;
    }

    // If we can't find the solution directory, try standard paths
    G.LogWarn("Solution directory not found via traversal. Checking standard paths...");

    var baseDir = startDir;

    // Check standard paths relative to bin/Debug or bin/Release
    var possiblePaths = new[]
    {
        Path.Combine(baseDir, "..", "..", ".."), // For bin/Debug/net9.0
        Path.Combine(baseDir, "..", "..", "..", ".."), // For bin/Debug
        Path.Combine(baseDir, "..", ".."), // For published app
        Path.Combine(baseDir, "..", "..", "..", "..", ".."), // For test runners
        baseDir // Current directory
    };

    foreach (var path in possiblePaths)
    {
      try
      {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(Path.Combine(fullPath, "Ghost.Core")) &&
            Directory.Exists(Path.Combine(fullPath, "Ghost.SDK")))
        {
          return fullPath;
        }
      }
      catch
      {
        // Invalid path, skip
      }
    }

    return null;
  }

  /// <summary>
  /// Builds a project using dotnet CLI
  /// </summary>
  private async Task<bool> BuildProjectAsync(string projectDir, string projectFile)
  {
    try
    {
      var psi = new ProcessStartInfo
      {
          FileName = "dotnet",
          Arguments = $"build \"{projectFile}\" -c Release",
          WorkingDirectory = projectDir,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
      };

      G.LogInfo($"Building project: dotnet {psi.Arguments} in {projectDir}");

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
        G.LogError($"Output: {output}");
        return false;
      }

      G.LogInfo($"Successfully built {projectFile}");
      return true;
    }
    catch (Exception ex)
    {
      G.LogError(ex, $"Error building project in {projectDir}");
      return false;
    }
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

  private async Task TerminateGhostProcessesAsync()
  {
    try
    {
      AnsiConsole.MarkupLine("[grey]Checking for running Ghost processes...[/]");

      var ghostProcesses = Process.GetProcesses()
          .Where(p =>
          {
            try
            {
              return p.ProcessName.ToLowerInvariant().Contains("ghost") ||
                     (p.MainModule?.FileName?.Contains("\\Ghost\\", StringComparison.OrdinalIgnoreCase) ?? false);
            }
            catch
            {
              // Process access might be denied, skip it
              return false;
            }
          })
          .ToList();

      if (ghostProcesses.Count > 0)
      {
        AnsiConsole.MarkupLine($"[yellow]Found {ghostProcesses.Count} running Ghost processes that need to be terminated:[/]");

        foreach (var process in ghostProcesses)
        {
          try
          {
            string processDetails = $"{process.ProcessName} (PID: {process.Id})";
            try
            {
              if (process.MainModule != null)
              {
                processDetails += $" - {process.MainModule.FileName}";
              }
            }
            catch
            {
              // Ignore if we can't get the module info
            }

            AnsiConsole.MarkupLine($" [grey]· {processDetails}[/]");
          }
          catch
          {
            AnsiConsole.MarkupLine($" [grey]· Unknown process (PID: {process.Id})[/]");
          }
        }

        if (!_settings.Force)
        {
          var confirmKill = AnsiConsole.Confirm("[yellow]Would you like to terminate these processes?[/]", true);
          if (!confirmKill)
          {
            throw new GhostException(
                "Installation cannot proceed while Ghost processes are running. Please terminate them manually or use --force.",
                ErrorCode.InstallationError);
          }
        }

        foreach (var process in ghostProcesses)
        {
          try
          {
            AnsiConsole.MarkupLine($"[grey]Terminating process: {process.ProcessName} (PID: {process.Id})[/]");
            process.Kill(true); // true = kill entire process tree
            await Task.Delay(500); // Brief delay to ensure process is terminated
          }
          catch (Exception ex)
          {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not terminate process {process.Id}: {ex.Message}");
          }
        }

        // Give processes time to properly shut down
        AnsiConsole.MarkupLine("[grey]Waiting for processes to terminate...[/]");
        await Task.Delay(2000);

        // Double-check if all processes were terminated
        var remainingProcesses = Process.GetProcesses()
            .Where(p =>
            {
              try
              {
                return p.ProcessName.ToLowerInvariant().Contains("ghost") ||
                       (p.MainModule?.FileName?.Contains("\\Ghost\\", StringComparison.OrdinalIgnoreCase) ?? false);
              }
              catch
              {
                return false;
              }
            })
            .ToList();

        if (remainingProcesses.Count > 0 && _settings.Force)
        {
          AnsiConsole.MarkupLine("[yellow]Warning:[/] Some Ghost processes could not be terminated. Installation may fail.");
        } else if (remainingProcesses.Count > 0)
        {
          throw new GhostException(
              "Could not terminate all Ghost processes. Please terminate them manually or use --force.",
              ErrorCode.InstallationError);
        } else
        {
          AnsiConsole.MarkupLine("[green]All Ghost processes terminated successfully.[/]");
        }
      } else
      {
        AnsiConsole.MarkupLine("[grey]No running Ghost processes found.[/]");
      }
    }
    catch (Exception ex) when (!(ex is GhostException))
    {
      AnsiConsole.MarkupLine($"[yellow]Warning:[/] Error checking for Ghost processes: {ex.Message}");

      if (!_settings.Force)
      {
        throw new GhostException(
            "Could not check for running Ghost processes. Please ensure no Ghost processes are running or use --force.",
            ex,
            ErrorCode.InstallationError);
      }
    }
  }

  /// <summary>
/// Updates templates with dependency information to ensure they work with local library references
/// </summary>
private async Task UpdateTemplatesWithDependenciesAsync(string templatesDir, string libsDir, StatusContext ctx)
{
    try
    {
        ctx.Status("Updating templates with dependency information...");

        // Get a list of all project template files
        var csprojTemplates = Directory.GetFiles(templatesDir, "*.csproj.tpl", SearchOption.AllDirectories);

        if (csprojTemplates.Length == 0)
        {
            G.LogInfo("No project templates found to update");
            return;
        }

        G.LogInfo($"Found {csprojTemplates.Length} project templates to update");

        // Get the list of MS Extensions DLLs in the libs directory
        var msExtensionsDlls = Directory.GetFiles(libsDir, "Microsoft.Extensions.*.dll")
            .Select(Path.GetFileName)
            .OrderBy(f => f)
            .ToList();

        // Create the packageReferences section for the template
        var packageRefs = new StringBuilder();
        packageRefs.AppendLine("    <!-- Required dependencies when using local libs -->");

        foreach (var dll in msExtensionsDlls)
        {
            var packageName = Path.GetFileNameWithoutExtension(dll);
            packageRefs.AppendLine($"    <PackageReference Include=\"{packageName}\" Version=\"$(MicrosoftExtensionsVersion)\" Condition=\"'{{{{ use_local_libs }}}}' == 'true'\" />");
        }

        // Update each template
        foreach (var templateFile in csprojTemplates)
        {
            var templateName = Path.GetFileName(templateFile);
            ctx.Status($"Updating template: {templateName}...");

            var content = await File.ReadAllTextAsync(templateFile);

            // Check if the template already has our versions defined
            if (!content.Contains("<MicrosoftExtensionsVersion>"))
            {
                // Add the property group for versions
                content = content.Replace("<PropertyGroup>",
                    "<PropertyGroup>\n" +
                    "    <MicrosoftExtensionsVersion>9.0.0</MicrosoftExtensionsVersion>");
            }

            // Check if template already has required dependencies pattern
            if (!content.Contains("<!-- Required dependencies when using local libs -->"))
            {
                // Find where to insert dependencies
                var insertPoint = content.IndexOf("<PackageReference Include=\"Ghost.SDK\"");
                if (insertPoint > 0)
                {
                    // Insert before the Ghost.SDK PackageReference
                    var beforeInsert = content.Substring(0, insertPoint);
                    var afterInsert = content.Substring(insertPoint);

                    content = beforeInsert + packageRefs.ToString() + "\n    " + afterInsert;
                }
            }

            // Update references to use Private=false to avoid copying the DLLs
            if (content.Contains("<Reference Include=\"Ghost.SDK\"") && !content.Contains("<Private>false</Private>"))
            {
                content = content.Replace("<HintPath>$(GhostInstallDir)\\libs\\Ghost.SDK.dll</HintPath>",
                    "<HintPath>$(GhostInstallDir)\\libs\\Ghost.SDK.dll</HintPath>\n      <Private>false</Private>");

                content = content.Replace("<HintPath>$(GhostInstallDir)\\libs\\Ghost.Core.dll</HintPath>",
                    "<HintPath>$(GhostInstallDir)\\libs\\Ghost.Core.dll</HintPath>\n      <Private>false</Private>");
            }

            // Write updated content back to the template file
            await File.WriteAllTextAsync(templateFile, content);
            G.LogInfo($"Updated template: {templateName}");
        }

        G.LogInfo("All templates have been updated with dependency information");
    }
    catch (Exception ex)
    {
        G.LogError(ex, "Failed to update templates with dependency information");
    }
}
}
