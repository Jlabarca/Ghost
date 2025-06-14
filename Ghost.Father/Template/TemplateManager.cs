using System.Diagnostics;
using System.Text.Json;
using Ghost.Exceptions;
using Spectre.Console;
namespace Ghost.Templates;

public class TemplateManager
{
    private readonly string _templatesPath;
    private Dictionary<string, TemplateInfo> _templates;

    public TemplateManager(string templatesPath)
    {
        _templatesPath = templatesPath;
        _templates = new Dictionary<string, TemplateInfo>();
        LoadTemplates();
    }

    private void LoadTemplates()
    {
        _templates = new Dictionary<string, TemplateInfo>();
        string[]? templateDirs = Directory.GetDirectories(_templatesPath);

        foreach (string? templateDir in templateDirs)
        {
            string? templateJsonPath = Path.Combine(templateDir, "template.json");
            if (!File.Exists(templateJsonPath))
            {
                continue;
            }

            string? templateJson = File.ReadAllText(templateJsonPath);
            TemplateInfo? template = JsonSerializer.Deserialize<TemplateInfo>(templateJson);

            if (template != null)
            {
                template.LoadTemplateInfo(templateDir);
                _templates[template.Name] = template;
            }
        }
    }

    public async Task<DirectoryInfo> CreateFromTemplateAsync(string templateName, string projectName, string outputPath)
    {
        if (!_templates.TryGetValue(templateName, out TemplateInfo? template))
        {
            throw new GhostException($"Template '{templateName}' not found", ErrorCode.TemplateNotFound);
        }

        // Create project directory
        DirectoryInfo? projectDir = Directory.CreateDirectory(Path.Combine(outputPath, projectName));
        string? templateFilesPath = Path.Combine(template.TemplateRoot, "files");

        if (!Directory.Exists(templateFilesPath))
        {
            throw new GhostException($"Template files not found for '{templateName}'", ErrorCode.TemplateCorrupted);
        }

        // Copy and process all template files
        await ProcessTemplateFilesAsync(templateFilesPath, projectDir.FullName, projectName, template);

        return projectDir;
    }

    private async Task ProcessTemplateFilesAsync(string sourcePath, string targetPath, string projectName,
            TemplateInfo template)
    {
        foreach (string? file in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
        {
            string? relativePath = Path.GetRelativePath(sourcePath, file);
            string? processedPath = ProcessTemplateString(relativePath, projectName, template);
            string? targetFilePath = Path.Combine(targetPath, processedPath);

            // Create target directory if it doesn't exist
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath));

            // Process file content if it's a template
            if (file.EndsWith(".tpl"))
            {
                string? content = await File.ReadAllTextAsync(file);
                string? processedContent = ProcessTemplateString(content, projectName, template);
                targetFilePath = targetFilePath[..^4]; // Remove .tpl extension
                await File.WriteAllTextAsync(targetFilePath, processedContent);
            }
            else
            {
                File.Copy(file, targetFilePath, true);
            }
        }
    }

    private string ProcessTemplateString(string input, string projectName, TemplateInfo template)
    {
        // Initialize variables dictionary with base values
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
                ["project_name"] = projectName,
                ["safe_name"] = MakeSafeName(projectName)
        };

        // Add template variables without processing them
        foreach ((string? key, string? value) in template.Variables)
        {
            if (!variables.ContainsKey(key))
            {
                variables[key] = value;
            }
        }

        // Process all template variables in one pass
        string? result = input;
        bool madeChange;
        int maxIterations = 10; // Prevent infinite loops

        do
        {
            madeChange = false;
            string? currentResult = result;

            foreach ((string? key, string? value) in variables)
            {
                string? placeholder = $"{{{{ {key} }}}}";
                if (currentResult.Contains(placeholder))
                {
                    currentResult = currentResult.Replace(placeholder, value);
                    madeChange = true;
                }
            }

            result = currentResult;
            maxIterations--;
        } while (madeChange && maxIterations > 0);

        if (maxIterations == 0)
        {
            G.LogWarn($"Template processing reached max iterations for input: {input}");
        }

        return result;
    }

    private static string MakeSafeName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "GhostApp";
        }

        // Convert to PascalCase and remove invalid characters
        string[]? words = name.Split(new[]
        {
                ' ', '-',
                '_', '.',
                '/', '\\'
        }, StringSplitOptions.RemoveEmptyEntries);

        string? safeName = string.Join("", words.Select(word =>
                string.Concat(
                        char.ToUpper(word[0]),
                        word.Length > 1 ? word[1..].ToLower() : ""
                )
        ));

        // Ensure it starts with a letter
        if (safeName.Length == 0 || !char.IsLetter(safeName[0]))
        {
            safeName = "Ghost" + safeName;
        }

        return safeName;
    }

    public IReadOnlyDictionary<string, TemplateInfo> GetAvailableTemplates()
    {
        return _templates;
    }

    public async Task<TemplateInfo> GetTemplateAsync(string settingsTemplate)
    {
        return await Task.FromResult(_templates[settingsTemplate]);
    }

    public async Task InstallTemplatesAsync(string targetTemplatesDir, bool force, StatusContext ctx)
    {
        // Find the Template directory
        string? sourceTemplatesPath = FindTemplatesDirectory();

        if (sourceTemplatesPath == null)
        {
            G.LogWarn("Templates directory not found. Skipping template installation.");
            return;
        }

        // Check if source and target are the same to avoid copying to itself
        string? normalizedSource = Path.GetFullPath(sourceTemplatesPath).TrimEnd(Path.DirectorySeparatorChar);
        string? normalizedTarget = Path.GetFullPath(targetTemplatesDir).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            G.LogInfo("Template source and target are the same. Skipping template copying.");
            return;
        }

        // Create the target directory if it doesn't exist
        Directory.CreateDirectory(targetTemplatesDir);

        // Copy template files
        await CopyTemplatesAsync(sourceTemplatesPath, targetTemplatesDir, force, ctx);
    }

    /// <summary>
    ///     Finds the templates directory in various standard locations
    /// </summary>
    private string FindTemplatesDirectory()
    {
        string? executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (executablePath == null)
        {
            G.LogError("Could not determine executable path");
            return null;
        }

        string? sourceDir = Path.GetDirectoryName(executablePath);
        if (sourceDir == null)
        {
            G.LogError("Could not determine source directory");
            return null;
        }

        // Look for Template directory in various locations
        string[]? possibleTemplatePaths = new[]
        {
                // Direct Path to Ghost.Father/Template
                Path.Combine(sourceDir, "..", "..", "..", "Ghost.Father", "Template"),
                // For development environment
                Path.Combine(sourceDir, "..", "..", "..", "Template"),
                // If executable is in bin/Debug/net9.0
                Path.Combine(sourceDir, "..", "..", "..", "..", "Template"),
                // Fallback to old paths
                Path.Combine(sourceDir, "Template"), Path.Combine(sourceDir, "Templates")
        };

        foreach (string? path in possibleTemplatePaths)
        {
            try
            {
                string? fullPath = Path.GetFullPath(path);
                G.LogInfo($"Checking for templates directory: {fullPath}");

                if (Directory.Exists(fullPath))
                {
                    G.LogInfo($"Found templates directory: {fullPath}");
                    return fullPath;
                }
            }
            catch (Exception ex)
            {
                G.LogWarn($"Error checking template path {path}: {ex.Message}");
            }
        }

        return null;
    }

    /// <summary>
    ///     Copies templates from source to target directory
    /// </summary>
    private async Task CopyTemplatesAsync(string sourcePath, string targetPath, bool force, StatusContext ctx)
    {
        // Check if there are subdirectories that represent individual templates
        string[]? templateFolders = Directory.GetDirectories(sourcePath);

        if (templateFolders.Length > 0)
        {
            // If there are subdirectories, assume each is a template
            G.LogInfo($"Found {templateFolders.Length} templates to install");

            // Copy each template folder
            foreach (string? templateFolder in templateFolders)
            {
                string? templateName = Path.GetFileName(templateFolder);
                ctx.Status($"Installing template: {templateName}...");
                string? targetFolder = Path.Combine(targetPath, templateName);

                // Check if template already exists
                bool shouldCopy = true;
                if (Directory.Exists(targetFolder) && !force)
                {
                    bool replaceTemplate = AnsiConsole.Confirm(
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
        else
        {
            // If there are no subdirectories, copy the entire Template folder content
            G.LogInfo("Copying entire Template directory content");

            string[]? files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                // Copy all files and directories
                await CopyDirectoryAsync(sourcePath, targetPath);
                G.LogInfo($"Installed templates from: {sourcePath}");
            }
            else
            {
                G.LogWarn($"Template directory {sourcePath} exists but is empty. Nothing to copy.");
            }
        }
    }

    /// <summary>
    ///     Recursively copies a directory and its contents
    /// </summary>
    private async Task CopyDirectoryAsync(string sourceDir, string targetDir)
    {
        // Create the target directory
        Directory.CreateDirectory(targetDir);

        // Copy all files
        foreach (string? file in Directory.GetFiles(sourceDir))
        {
            string? targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            await CopyFileAsync(file, targetFile);
        }

        // Copy all subdirectories recursively
        foreach (string? dir in Directory.GetDirectories(sourceDir))
        {
            string? targetSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
            await CopyDirectoryAsync(dir, targetSubDir);
        }
    }

    /// <summary>
    ///     Copies a file with retry logic
    /// </summary>
    private async Task CopyFileAsync(string sourcePath, string targetPath)
    {
        const int maxRetries = 3;
        int retryCount = 0;

        while (retryCount < maxRetries)
        {
            try
            {
                using (FileStream sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read))
                using (FileStream targetStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                {
                    await sourceStream.CopyToAsync(targetStream);
                }

                return; // Success
            }
            catch (IOException)
            {
                retryCount++;
                if (retryCount >= maxRetries)
                {
                    G.LogWarn($"Failed to copy file: {sourcePath} to {targetPath} after {maxRetries} attempts");
                    throw; // Rethrow the exception after all retries failed
                }

                await Task.Delay(500 * retryCount); // Exponential backoff
            }
        }
    }
}
