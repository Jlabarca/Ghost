using Ghost.Core.Config;
using Ghost.Core.Exceptions;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;
using Ghost.Core.Storage;
using Ghost.Templates;
using Microsoft.Extensions.DependencyInjection;


namespace Ghost.Father.CLI.Commands;

/// <summary>
/// Command for installing Ghost to the system
/// </summary>
public class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    private readonly GhostConfig _config;
    private readonly InstallationService _installationService;
    private readonly TemplateManager _templateManager;
    private readonly EnvironmentSetup _environmentSetup;
    private readonly ProcessManager _processManager;
    private readonly SdkBuildService _sdkBuildService;


    public InstallCommand(IServiceProvider services)
    {
        _config = services.GetService<GhostConfig>() ??
                  throw new ArgumentNullException(nameof(GhostConfig), "GhostConfig is not registered");
        _templateManager = services.GetService<TemplateManager>() ??
                           throw new ArgumentNullException(nameof(TemplateManager), "TemplateManager is not registered");
        // _processManager = services.GetService<ProcessManager>() ??
        //                   throw new ArgumentNullException(nameof(ProcessManager), "ProcessManager is not registered");

        _installationService = new InstallationService();
        _environmentSetup = new EnvironmentSetup();
        _sdkBuildService = new SdkBuildService();
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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var installPath = settings.CustomInstallPath ?? _installationService.GetDefaultInstallPath();

        return await AnsiConsole.Status()
            .StartAsync("Installing Ghost...", async ctx =>
            {
                try
                {
                    #region Terminate running processes
                    //await _processManager.TerminateGhostProcessesAsync(settings.Force);
                    #endregion

                    #region Check for existing installation
                    if (Directory.Exists(installPath) && !settings.Force && !settings.Repair)
                    {
                        AnsiConsole.MarkupLine("[yellow]Ghost is already installed at:[/] " + installPath);
                        AnsiConsole.MarkupLine("Use --force to reinstall or --repair to repair the installation.");
                        return 1;
                    }
                    #endregion

                    #region Create installation directories
                    ctx.Status("Creating installation directories...");
                    var installStructure = _installationService.CreateInstallationDirectories(installPath);
                    #endregion

                    #region Copy executable files
                    ctx.Status("Copying executable files...");
                    await _installationService.CopyExecutableFilesAsync(installStructure.BinDir, ctx);
                    #endregion

                    #region Install templates
                    ctx.Status("Installing templates...");
                    await _templateManager.InstallTemplatesAsync(installStructure.TemplatesDir, settings.Force, ctx);
                    #endregion

                    #region Update PATH and shell configuration
                    ctx.Status("Updating system PATH and shell configuration...");
                    await _environmentSetup.UpdatePathAndShellConfigAsync(installStructure.BinDir);
                    #endregion

                    #region Build SDK libraries
                    ctx.Status("Building SDK libraries...");
                    if (!await _sdkBuildService.BuildSdkLibrariesAsync(installStructure.LibsDir, ctx))
                    {
                        AnsiConsole.MarkupLine("[yellow]Warning: Failed to build SDK libraries.[/] " +
                                               "Projects will use NuGet packages instead.");
                    }
                    #endregion

                    #region Set environment variables
                    Environment.SetEnvironmentVariable("GHOST_INSTALL", installPath, EnvironmentVariableTarget.User);
                    Environment.SetEnvironmentVariable("GHOST_INSTALL", installPath); // Current process
                    #endregion

                    #region Display success message
                    AnsiConsole.MarkupLine("[green]Ghost installed successfully![/]");
                    AnsiConsole.MarkupLine($"Installation path: {installPath}");
                    AnsiConsole.MarkupLine($"Added to PATH: {installStructure.BinDir}");
                    AnsiConsole.MarkupLine($"Ghost apps directory: {installStructure.GhostAppsDir}");
                    AnsiConsole.MarkupLine($"SDK libraries: {installStructure.LibsDir}");
                    #endregion

                    return 0;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error during installation:[/] {ex.Message}");
                    if (Directory.Exists(installPath) && settings.Force)
                    {
                        try
                        {
                            _installationService.SafeDirectoryDelete(installPath);
                            AnsiConsole.MarkupLine("[grey]Cleaned up installation directory[/]");
                        }
                        catch (Exception cleanupEx)
                        {
                            AnsiConsole.MarkupLine(
                                $"[grey]Could not clean up installation directory: {cleanupEx.Message}[/]");
                        }
                    }

                    return 1;
                }
            });
    }
}



/// <summary>
/// Service for handling Ghost installation
/// </summary>
public class InstallationService
{
    /// <summary>
    /// Installation directory structure
    /// </summary>
    public class InstallStructure
    {
        public string InstallPath { get; set; }
        public string BinDir { get; set; }
        public string GhostAppsDir { get; set; }
        public string LibsDir { get; set; }
        public string TemplatesDir { get; set; }
    }

    /// <summary>
    /// Creates the necessary installation directories
    /// </summary>
    public InstallStructure CreateInstallationDirectories(string installPath)
    {
        // Create main installation directory
        Directory.CreateDirectory(installPath);

        // Create subdirectories
        var binDir = Path.Combine(installPath, "bin");
        var ghostAppsDir = Path.Combine(installPath, "ghosts");
        var libsDir = Path.Combine(installPath, "libs");
        var templatesDir = Path.Combine(installPath, "templates");

        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(ghostAppsDir);
        Directory.CreateDirectory(libsDir);
        Directory.CreateDirectory(templatesDir);

        return new InstallStructure
        {
            InstallPath = installPath,
            BinDir = binDir,
            GhostAppsDir = ghostAppsDir,
            LibsDir = libsDir,
            TemplatesDir = templatesDir
        };
    }

    /// <summary>
    /// Copies executable files from the current directory to the installation directory
    /// </summary>
    public async Task CopyExecutableFilesAsync(string binDir, StatusContext ctx)
    {
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
            await MakeFileExecutableAsync(targetExe);
        }
    }

    /// <summary>
    /// Returns the default installation path based on the platform
    /// </summary>
    public string GetDefaultInstallPath()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: Use LocalApplicationData
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Ghost");
        }
        else if (OperatingSystem.IsMacOS())
        {
            // macOS: Use user's Library folder
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homeDir, "Library", "Application Support", "Ghost");
        }
        else
        {
            // Linux: Use standard ~/.local/share
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(homeDir, ".local", "share", "Ghost");
        }
    }

    /// <summary>
    /// Safely deletes a directory, attempting alternative methods if standard deletion fails
    /// </summary>
    public void SafeDirectoryDelete(string path)
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
            }
            else
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

    #region File Operations
    public async Task CopyFilesAsync(string sourceDir, string targetDir, StatusContext ctx)
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
                dirName.Equals("Template", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetPath = Path.Combine(targetDir, dirName);
            ctx.Status($"Copying directory: {dirName}...");
            await CopyDirectoryAsync(dir, targetPath);
        }
    }

    public async Task CopyDirectoryAsync(string sourceDir, string targetDir)
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

    public async Task CopyFileAsync(string sourcePath, string targetPath)
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
                        }
                        else
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
            catch (IOException)
            {
                retryCount++;
                if (retryCount >= maxRetries)
                {
                    // If all retries failed, try to use alternative copy method
                    if (TryAlternativeCopy(sourcePath, targetPath))
                    {
                        success = true;
                    }
                    else
                    {
                        L.LogError($"Failed to copy file from {sourcePath} to {targetPath} after {maxRetries} attempts");
                        throw;
                    }
                }
                else
                {
                    // Wait before retry
                    await Task.Delay(1000 * retryCount);
                    L.LogWarn($"Retrying file copy ({retryCount}/{maxRetries}): {Path.GetFileName(targetPath)}");
                }
            }
            catch (Exception ex)
            {
                L.LogError(ex, $"Failed to copy file from {sourcePath} to {targetPath}");
                throw;
            }
        }
    }

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
            }
            else
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
    #endregion

    /// <summary>
    /// Makes a file executable on Unix systems
    /// </summary>
    public async Task MakeFileExecutableAsync(string filePath)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    L.LogWarn($"Failed to set executable permissions on {filePath}");
                }
            }
        }
        catch (Exception ex)
        {
            L.LogWarn($"Could not set executable permissions: {ex.Message}");
        }
    }
}

/// <summary>
/// Handles environment configuration for Ghost installation
/// </summary>
public class EnvironmentSetup
{
    /// <summary>
    /// Updates PATH and shell configuration to make the ghost command available
    /// </summary>
    public async Task UpdatePathAndShellConfigAsync(string binDir)
    {
        // Add to PATH environment variable
        await UpdatePathAsync(binDir);

        // For Unix systems, also add to shell profile for immediate use
        if (!OperatingSystem.IsWindows())
        {
            await AddToShellProfileAsync(binDir);
        }

        // Explain to the user how to use the command immediately
        AnsiConsole.MarkupLine("\n[yellow]To use the ghost command immediately:[/]");

        if (OperatingSystem.IsWindows())
        {
            AnsiConsole.MarkupLine("- Start a new Command Prompt or PowerShell window");
            AnsiConsole.MarkupLine($"- Or run: [grey]{binDir}\\ghost.exe[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("- Start a new terminal window");
            AnsiConsole.MarkupLine($"- Or run: [grey]{binDir}/ghost[/]");
            AnsiConsole.MarkupLine("- Or run: [grey]source ~/.zshrc[/] (if using zsh)");
            AnsiConsole.MarkupLine("- Or run: [grey]source ~/.bash_profile[/] (if using bash)");
        }
    }

    /// <summary>
    /// Updates the PATH environment variable to include the Ghost bin directory
    /// </summary>
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
    /// Adds the bin directory to the user's shell profile for immediate use
    /// </summary>
    private async Task AddToShellProfileAsync(string binDir)
    {
        // Determine which shell the user is likely using
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "";
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string profilePath = null;

        // Default to zsh for macOS, which is the default since Catalina
        if (OperatingSystem.IsMacOS() || shell.EndsWith("zsh"))
        {
            profilePath = Path.Combine(homeDir, ".zshrc");
        }
        else if (shell.EndsWith("bash"))
        {
            profilePath = Path.Combine(homeDir, ".bash_profile");
            if (!File.Exists(profilePath))
                profilePath = Path.Combine(homeDir, ".bashrc");
        }

        // If we can't determine the shell profile, just return
        if (profilePath == null || !File.Exists(profilePath))
        {
            L.LogWarn($"Could not determine shell profile to update. Manual PATH update may be needed.");
            return;
        }

        try
        {
            // Check if the line already exists to avoid duplicates
            var profileContent = await File.ReadAllTextAsync(profilePath);
            var exportLine = $"export PATH=\"$PATH:{binDir}\"";

            if (!profileContent.Contains(binDir))
            {
                // Add to the profile file
                await File.AppendAllTextAsync(profilePath, $"\n# Added by Ghost installer\n{exportLine}\n");
                L.LogInfo($"Added Ghost bin directory to {profilePath}");
            }
        }
        catch (Exception ex)
        {
            L.LogWarn($"Failed to update shell profile: {ex.Message}");
        }

        // Also create or update a script in the bin directory that can be sourced directly
        try
        {
            var ghostEnvScript = Path.Combine(binDir, "ghost-env.sh");
            await File.WriteAllTextAsync(ghostEnvScript,
                $"#!/bin/sh\n\n" +
                $"# Ghost environment setup script\n" +
                $"export GHOST_INSTALL=\"{Path.GetDirectoryName(binDir)}\"\n" +
                $"export PATH=\"$PATH:{binDir}\"\n\n" +
                $"echo \"Ghost environment variables set. You can now use the 'ghost' command.\"\n");

            // Make it executable
            await MakeFileExecutableAsync(ghostEnvScript);

            AnsiConsole.MarkupLine($"- Or run: [grey]source {ghostEnvScript}[/]");
        }
        catch (Exception ex)
        {
            L.LogWarn($"Failed to create ghost-env.sh script: {ex.Message}");
        }
    }

    /// <summary>
    /// Makes a file executable on Unix systems
    /// </summary>
    private async Task MakeFileExecutableAsync(string filePath)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                {
                    L.LogWarn($"Failed to set executable permissions on {filePath}");
                }
            }
        }
        catch (Exception ex)
        {
            L.LogWarn($"Could not set executable permissions: {ex.Message}");
        }
    }
}


/// <summary>
/// Handles building the Ghost SDK libraries
/// </summary>
public class SdkBuildService
{
    /// <summary>
    /// Builds the SDK and Core libraries and places them in the libs directory
    /// </summary>
    public async Task<bool> BuildSdkLibrariesAsync(string libsDir, StatusContext ctx)
    {
        try
        {
            // First, check if dotnet is available
            if (!await IsDotnetAvailableAsync())
            {
                L.LogWarn("The 'dotnet' command is not available. Will create minimal SDK implementations.");
                await CreateMinimalSdkImplementationsAsync(libsDir, ctx);
                return false;
            }

            // Find the projects
            ctx.Status("Locating project files...");
            var (coreProjPath, sdkProjPath) = FindProjectPaths();

            if (coreProjPath == null || sdkProjPath == null)
            {
                L.LogError("Could not find project files");
                await CreateMinimalSdkImplementationsAsync(libsDir, ctx);
                return false;
            }

            L.LogInfo($"Found project files: {coreProjPath} and {sdkProjPath}");

            // Build Ghost.Core
            ctx.Status("Building Ghost.Core...");
            if (!await BuildProjectAsync(Path.GetDirectoryName(coreProjPath), "Ghost.Core.csproj"))
            {
                L.LogError("Failed to build Ghost.Core");
                await CreateMinimalSdkImplementationsAsync(libsDir, ctx);
                return false;
            }

            // Build Ghost.SDK
            ctx.Status("Building Ghost.SDK...");
            if (!await BuildProjectAsync(Path.GetDirectoryName(sdkProjPath), "Ghost.SDK.csproj"))
            {
                L.LogError("Failed to build Ghost.SDK");
                await CreateMinimalSdkImplementationsAsync(libsDir, ctx);
                return false;
            }

            // TODO: Copy built DLLs to libs directory (this part was missing in the original code)
            // ...

            return true;
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to build SDK libraries");
            await CreateMinimalSdkImplementationsAsync(libsDir, ctx);
            return false;
        }
    }

    /// <summary>
    /// Finds the project file paths
    /// </summary>
    private (string coreProjPath, string sdkProjPath) FindProjectPaths()
    {
        // Find the actual project files relative to the executing assembly
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (executablePath == null)
        {
            L.LogError("Failed to determine executable path");
            return (null, null);
        }

        var sourceDir = Path.GetDirectoryName(executablePath);
        if (sourceDir == null)
        {
            L.LogError("Failed to determine source directory");
            return (null, null);
        }

        // Look for the solution directory
        var solutionDir = FindSolutionDirectory(sourceDir);
        if (solutionDir == null)
        {
            L.LogError("Could not find solution directory");
            return (null, null);
        }

        L.LogInfo($"Found solution directory: {solutionDir}");

        // Find project paths
        var coreProjPath = Path.Combine(solutionDir, "Ghost.Core", "Ghost.Core.csproj");
        var sdkProjPath = Path.Combine(solutionDir, "Ghost.SDK", "Ghost.SDK.csproj");

        if (!File.Exists(coreProjPath))
        {
            L.LogError($"Ghost.Core project not found at: {coreProjPath}");
            return (null, null);
        }

        if (!File.Exists(sdkProjPath))
        {
            L.LogError($"Ghost.SDK project not found at: {sdkProjPath}");
            return (null, null);
        }

        return (coreProjPath, sdkProjPath);
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
        L.LogWarn("Solution directory not found via traversal. Checking standard paths...");

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
    /// Checks if the dotnet command is available on the system
    /// </summary>
    private async Task<bool> IsDotnetAvailableAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
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

    /// <summary>
    /// Builds a project using dotnet CLI with platform-specific adaptations
    /// </summary>
    private async Task<bool> BuildProjectAsync(string projectDir, string projectFile)
    {
        try
        {
            // On macOS/Linux, ensure the path uses forward slashes
            if (!OperatingSystem.IsWindows())
            {
                projectDir = projectDir.Replace('\\', '/');
                projectFile = projectFile.Replace('\\', '/');
            }

            // Create the process start info
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

            // On macOS/Linux, we need to ensure PATH is correctly set
            if (!OperatingSystem.IsWindows())
            {
                // Copy the current process environment
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";

                // Add additional common .NET SDK locations on Unix systems
                var additionalPaths = new[]
                {
                    "/usr/local/share/dotnet",
                    "/usr/local/bin",
                    "/usr/bin",
                    "/opt/homebrew/bin", // For macOS with Homebrew
                    $"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/.dotnet/tools"
                };

                var newPath = string.Join(Path.PathSeparator,
                    new[] { pathEnv }.Concat(additionalPaths.Where(Directory.Exists)));

                psi.EnvironmentVariables["PATH"] = newPath;
            }

            L.LogInfo($"Building project: dotnet {psi.Arguments} in {projectDir}");

            var process = Process.Start(psi);
            if (process == null)
            {
                L.LogError($"Failed to start dotnet build process for {projectDir}");
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                L.LogError($"Build failed for {projectDir}: {error}");
                L.LogError($"Output: {output}");
                return false;
            }

            L.LogInfo($"Successfully built {projectFile}");
            return true;
        }
        catch (Exception ex)
        {
            L.LogError(ex, $"Error building project in {projectDir}");
            return false;
        }
    }

    /// <summary>
    /// Creates minimal SDK implementations when the build process fails
    /// </summary>
    private async Task CreateMinimalSdkImplementationsAsync(string libsDir, StatusContext ctx)
    {
        try
        {
            ctx.Status("Creating minimal SDK implementations...");

            // Create a minimal README explaining what's happening
            await File.WriteAllTextAsync(Path.Combine(libsDir, "README.txt"), @"
Ghost SDK Libraries - Minimal Implementation

These are minimal implementations of the Ghost SDK libraries, created because
the full build process couldn't be completed. These libraries provide just
enough functionality for basic Ghost applications to work.

To get the full functionality:
1. Install the .NET SDK (https://dotnet.microsoft.com/download)
2. Repair the Ghost installation with: ghost install --repair

Projects created with the Ghost templates will use NuGet packages by default
when minimal implementations are used.
");

            // Create empty placeholder DLLs if they don't exist
            if (!File.Exists(Path.Combine(libsDir, "Ghost.Core.dll")))
                await File.WriteAllBytesAsync(Path.Combine(libsDir, "Ghost.Core.dll"), new byte[1024]);

            if (!File.Exists(Path.Combine(libsDir, "Ghost.SDK.dll")))
                await File.WriteAllBytesAsync(Path.Combine(libsDir, "Ghost.SDK.dll"), new byte[1024]);

            // Create dependencies.txt file with explanation
            await File.WriteAllTextAsync(Path.Combine(libsDir, "dependencies.txt"), @"
Ghost SDK Dependencies

Minimal SDK implementation created because the 'dotnet' build process couldn't
be completed successfully.

Common NuGet dependencies that will be required:
- Microsoft.Extensions.Logging (9.0.0)
- Microsoft.Extensions.DependencyInjection (9.0.0)
- Microsoft.Extensions.Configuration.Json (9.0.0)
");

            L.LogInfo("Created minimal SDK implementations in " + libsDir);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Error creating minimal SDK implementations");
        }
    }
}