using Ghost.Core.Exceptions;
using Scriban;
using Scriban.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Ghost.Templates;

/// <summary>
/// Extension methods for TemplateManager to enhance Scriban template processing
/// </summary>
public static class TemplateManagerExtensions
{
    /// <summary>
    /// Creates a project from a template with additional template variables
    /// </summary>
    public static async Task<DirectoryInfo> CreateFromTemplateAsync(
        this TemplateManager templateManager,
        string templateName,
        string projectName,
        string outputPath,
        Dictionary<string, string> additionalVariables = null)
    {
        if (!templateManager.GetAvailableTemplates().TryGetValue(templateName, out var template))
        {
            throw new GhostException($"Template '{templateName}' not found", ErrorCode.TemplateNotFound);
        }

        // Create project directory
        var projectDir = Directory.CreateDirectory(Path.Combine(outputPath, projectName));
        var templateFilesPath = Path.Combine(template.TemplateRoot, "files");

        if (!Directory.Exists(templateFilesPath))
        {
            throw new GhostException($"Template files not found for '{templateName}'", ErrorCode.TemplateCorrupted);
        }

        // Copy and process all template files with additional variables
        await ProcessTemplateFilesAsync(templateFilesPath, projectDir.FullName, projectName, template, additionalVariables);

        return projectDir;
    }

    /// <summary>
    /// Processes template files with enhanced variable support
    /// </summary>
    private static async Task ProcessTemplateFilesAsync(
        string sourcePath,
        string targetPath,
        string projectName,
        TemplateInfo template,
        Dictionary<string, string> additionalVariables)
    {
        // Initialize Scriban template context
        var templateContext = new TemplateContext();
        var scriptObject = new ScriptObject();

        // Add default variables
        scriptObject.Add("project_name", projectName);
        scriptObject.Add("safe_name", MakeSafeName(projectName));

        // Add template variables
        foreach (var (key, value) in template.Variables)
        {
            if (!scriptObject.ContainsKey(key))
            {
                scriptObject.Add(key, value);
            }
        }

        // Add additional variables
        if (additionalVariables != null)
        {
            foreach (var (key, value) in additionalVariables)
            {
                if (!scriptObject.ContainsKey(key))
                {
                    scriptObject.Add(key, value);
                }
            }
        }

        // Add Ghost install directory if not already present
        if (!scriptObject.ContainsKey("ghost_install_dir"))
        {
            var ghostInstallDir = Environment.GetEnvironmentVariable("GHOST_INSTALL") ?? "";
            scriptObject.Add("ghost_install_dir", ghostInstallDir);
        }

        // Add SDK version if not already present
        if (!scriptObject.ContainsKey("sdk_version"))
        {
            scriptObject.Add("sdk_version", "1.0.0");
        }

        templateContext.PushGlobal(scriptObject);

        // Process each file
        foreach (var file in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, file);

            // Process the file path first to handle variables in the path
            var pathTemplate = Template.Parse(relativePath);
            var processedPath = await pathTemplate.RenderAsync(templateContext);

            var targetFilePath = Path.Combine(targetPath, processedPath);

            // Create target directory if it doesn't exist
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath));

            // Process file content if it's a template
            if (file.EndsWith(".tpl"))
            {
                var content = await File.ReadAllTextAsync(file);
                var contentTemplate = Template.Parse(content);
                var processedContent = await contentTemplate.RenderAsync(templateContext);

                // Remove .tpl extension
                targetFilePath = targetFilePath[..^4];

                await File.WriteAllTextAsync(targetFilePath, processedContent);
            }
            else
            {
                // Just copy non-template files
                File.Copy(file, targetFilePath, true);
            }
        }
    }

    /// <summary>
    /// Makes a safe name for C# identifiers
    /// </summary>
    private static string MakeSafeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "GhostApp";

        // Convert to PascalCase and remove invalid characters
        var words = name.Split(new[] { ' ', '-', '_', '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        var safeName = new StringBuilder();
        foreach (var word in words)
        {
            if (word.Length > 0)
            {
                safeName.Append(char.ToUpper(word[0]));
                if (word.Length > 1)
                {
                    safeName.Append(word.Substring(1).ToLower());
                }
            }
        }

        // Ensure it starts with a letter
        if (safeName.Length == 0 || !char.IsLetter(safeName[0]))
        {
            safeName.Insert(0, "Ghost");
        }

        return safeName.ToString();
    }
}