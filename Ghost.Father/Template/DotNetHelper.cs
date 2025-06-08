// src/Ghost/Templates/DotNetHelper.cs
using System.Diagnostics;
using System.Text.RegularExpressions;
using Ghost.Exceptions;
using Scriban;
namespace Ghost.Templates;

public static class DotNetHelper
{
    public static async Task<bool> IsPackageInstalledAsync(string packageName)
    {
        try
        {
            Process? process = new Process
            {
                    StartInfo = new ProcessStartInfo
                    {
                            FileName = "dotnet",
                            Arguments = OperatingSystem.IsWindows()
                                    ? $"list package | findstr {packageName}"
                                    : $"list package | grep {packageName}",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                    }
            };

            process.Start();
            await process.WaitForExitAsync();
            string? output = await process.StandardOutput.ReadToEndAsync();
            return !string.IsNullOrWhiteSpace(output);
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error checking package installation: {Package}", packageName);
            return false;
        }
    }

    public static async Task AddPackageAsync(
            string projectPath,
            string packageName,
            string version)
    {
        try
        {
            Process? process = new Process
            {
                    StartInfo = new ProcessStartInfo
                    {
                            FileName = "dotnet",
                            Arguments = $"add \"{projectPath}\" package {packageName} -v {version}",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = projectPath
                    }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                string? error = await process.StandardError.ReadToEndAsync();
                throw new GhostException(
                        $"Failed to add package {packageName}: {error}",
                        ErrorCode.TemplateError);
            }

            G.LogInfo($"Added package {packageName} v{version}");
        }
        catch (Exception ex) when (ex is not GhostException)
        {
            G.LogError(ex, "Error adding package: {Package}", packageName);
            throw new GhostException(
                    $"Failed to add package {packageName}",
                    ex,
                    ErrorCode.TemplateError);
        }
    }

    public static async Task<bool> IsDotNetInstalledAsync()
    {
        try
        {
            Process? process = new Process
            {
                    StartInfo = new ProcessStartInfo
                    {
                            FileName = "dotnet",
                            Arguments = "--version",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                    }
            };

            process.Start();
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    //TODO possible useful stuff to use later
    public static async Task<bool> CreateProjectAsync(
            string projectPath,
            string projectName,
            string template = "console")
    {
        try
        {
            Process? process = new Process
            {
                    StartInfo = new ProcessStartInfo
                    {
                            FileName = "dotnet",
                            Arguments = $"new {template} -n {projectName} -o \"{projectPath}\"",
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
                string? error = await process.StandardError.ReadToEndAsync();
                throw new GhostException(
                        $"Failed to create project: {error}",
                        ErrorCode.TemplateError);
            }

            return true;
        }
        catch (Exception ex) when (ex is not GhostException)
        {
            G.LogError(ex, "Error creating project: {Project}", projectName);
            throw new GhostException(
                    $"Failed to create project {projectName}",
                    ex,
                    ErrorCode.TemplateError);
        }
    }

    public static async Task<bool> BuildProjectAsync(string projectPath)
    {
        try
        {
            Process? process = new Process
            {
                    StartInfo = new ProcessStartInfo
                    {
                            FileName = "dotnet",
                            Arguments = "build",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            WorkingDirectory = projectPath
                    }
            };

            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                string? error = await process.StandardError.ReadToEndAsync();
                throw new GhostException(
                        $"Build failed: {error}",
                        ErrorCode.TemplateError);
            }

            return true;
        }
        catch (Exception ex) when (ex is not GhostException)
        {
            G.LogError(ex, "Error building project at: {Path}", projectPath);
            throw new GhostException(
                    "Failed to build project",
                    ex,
                    ErrorCode.TemplateError);
        }
    }

    public static async Task<string> GetPackageVersionAsync(string packageName)
    {
        try
        {
            Process? process = new Process
            {
                    StartInfo = new ProcessStartInfo
                    {
                            FileName = "dotnet",
                            Arguments = $"list package {packageName}",
                            RedirectStandardOutput = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                    }
            };

            process.Start();
            string? output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Parse version from output
            Match? versionMatch = Regex.Match(output, $@"{packageName}\s+(\d+\.\d+\.\d+)");
            return versionMatch.Success ? versionMatch.Groups[1].Value : null;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Error getting package version: {Package}", packageName);
            return null;
        }
    }

    public static async Task ApplyTemplateAsync(
            string projectPath,
            string templatePath,
            Dictionary<string, object> variables)
    {
        if (!Directory.Exists(templatePath))
        {
            throw new DirectoryNotFoundException($"Template path not found: {templatePath}");
        }

        // Copy template files
        foreach (string? file in Directory.GetFiles(templatePath, "*.*", SearchOption.AllDirectories))
        {
            string? relativePath = Path.GetRelativePath(templatePath, file);
            string? targetPath = Path.Combine(projectPath, relativePath);

            // Process file name if it contains template variables
            if (relativePath.Contains("{{") && relativePath.Contains("}}"))
            {
                Template? template = Template.Parse(relativePath);
                targetPath = Path.Combine(
                        projectPath,
                        template.Render(variables));
            }

            // Ensure target directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

            // Process file content if it's a template
            if (Path.GetExtension(file) == ".tpl")
            {
                string? content = await File.ReadAllTextAsync(file);
                Template? template = Template.Parse(content);
                await File.WriteAllTextAsync(
                        targetPath[..^4], // Remove .tpl
                        template.Render(variables));
            }
            else
            {
                File.Copy(file, targetPath, true);
            }
        }
    }
}
