using Ghost.Core.Config;
using Ghost.Core.Exceptions;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    private readonly GhostConfig _config;

    public InstallCommand(GhostConfig config)
    {
        _config = config;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("--force")]
        [Description("Force installation even if Ghost is already installed")]
        public bool Force { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var installPath = GetInstallPath();

        return await AnsiConsole.Status()
            .StartAsync("Installing Ghost...", async ctx =>
            {
                try
                {
                    if (Directory.Exists(installPath) && !settings.Force)
                    {
                        AnsiConsole.MarkupLine("[yellow]Ghost is already installed.[/]");
                        AnsiConsole.MarkupLine("Use --force to reinstall.");
                        return 1;
                    }

                    // Create installation directory
                    ctx.Status("Creating installation directory...");
                    Directory.CreateDirectory(installPath);

                    // Copy executable and dependencies
                    ctx.Status("Copying files...");
                    var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (executablePath == null)
                    {
                        throw new GhostException("Could not determine executable path", ErrorCode.InstallationError);
                    }

                    var sourceDir = Path.GetDirectoryName(executablePath);
                    await CopyFilesAsync(sourceDir!, installPath, ctx);

                    // Copy templates
                    ctx.Status("Installing templates...");
                    await InstallTemplatesAsync(sourceDir!, installPath, ctx);

                    // Add to PATH
                    ctx.Status("Updating system PATH...");
                    await UpdatePathAsync(installPath);

                    AnsiConsole.MarkupLine("[green]Ghost installed successfully![/]");
                    AnsiConsole.MarkupLine($"Installation path: {installPath}");
                    return 0;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error during installation:[/] {ex.Message}");
                    if (Directory.Exists(installPath))
                    {
                        try
                        {
                            Directory.Delete(installPath, true);
                        }
                        catch
                        {
                            // Ignore cleanup errors
                        }
                    }
                    return 1;
                }
            });
    }

    private async Task InstallTemplatesAsync(string sourceDir, string installPath, StatusContext ctx)
    {
        var sourceTemplatesPath = Path.Combine(sourceDir, "Templates");
        var targetTemplatesPath = Path.Combine(installPath, "templates");

        if (!Directory.Exists(sourceTemplatesPath))
        {
            G.LogWarn("Templates directory not found in source");
            return;
        }

        Directory.CreateDirectory(targetTemplatesPath);

        foreach (var templateDir in Directory.GetDirectories(sourceTemplatesPath))
        {
            var templateName = Path.GetFileName(templateDir);
            ctx.Status($"Installing template: {templateName}...");

            var targetTemplateDir = Path.Combine(targetTemplatesPath, templateName);
            await CopyDirectoryAsync(templateDir, targetTemplateDir);
        }
    }

    private async Task CopyFilesAsync(string sourceDir, string targetDir, StatusContext ctx)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            ctx.Status($"Copying {fileName}...");

            var targetPath = Path.Combine(targetDir, fileName);
            await CopyFileAsync(file, targetPath);
        }

        // Copy dependencies (dlls, etc.)
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.Equals("Templates", StringComparison.OrdinalIgnoreCase)) continue; // Skip templates dir

            var targetPath = Path.Combine(targetDir, dirName);
            await CopyDirectoryAsync(dir, targetPath);
        }
    }

    private async Task CopyDirectoryAsync(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            await CopyFileAsync(file, targetFile);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
            await CopyDirectoryAsync(dir, targetSubDir);
        }
    }

    private async Task CopyFileAsync(string sourcePath, string targetPath)
    {
        const int bufferSize = 81920; // 80 KB buffer
        await using var sourceStream
                = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, true);
        await using var targetStream
                = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, true);

        await sourceStream.CopyToAsync(targetStream);
    }

    private async Task UpdatePathAsync(string installPath)
    {
        var envTarget = EnvironmentVariableTarget.User;
        var currentPath = Environment.GetEnvironmentVariable("PATH", envTarget) ?? "";

        if (!currentPath.Contains(installPath))
        {
            var newPath = currentPath + Path.PathSeparator + installPath;
            Environment.SetEnvironmentVariable("PATH", newPath, envTarget);

            // Also update current process PATH
            Environment.SetEnvironmentVariable("PATH", newPath);
        }

        await Task.CompletedTask;
    }

    private string GetInstallPath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "Ghost");
    }
}