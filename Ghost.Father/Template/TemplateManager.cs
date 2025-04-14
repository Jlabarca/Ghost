using Ghost.Core;
using Ghost.Core.Exceptions;
using System.Text.Json;

namespace Ghost.Templates;

public class TemplateManager
{
    private readonly string _templatesPath;
    private Dictionary<string, TemplateInfo> _templates;

    public TemplateManager(string templatesPath)
    {
        _templatesPath = templatesPath;
        LoadTemplates();
    }

    private void LoadTemplates()
    {
        _templates = new Dictionary<string, TemplateInfo>();
        var templateDirs = Directory.GetDirectories(_templatesPath);

        foreach (var templateDir in templateDirs)
        {
            var templateJsonPath = Path.Combine(templateDir, "template.json");
            if (!File.Exists(templateJsonPath)) continue;

            var templateJson = File.ReadAllText(templateJsonPath);
            var template = JsonSerializer.Deserialize<TemplateInfo>(templateJson);

            if (template != null)
            {
                template.LoadTemplateInfo(templateDir);
                _templates[template.Name] = template;

            }
        }
    }

    public async Task<DirectoryInfo> CreateFromTemplateAsync(string templateName, string projectName, string outputPath)
    {
        if (!_templates.TryGetValue(templateName, out var template))
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

        // Copy and process all template files
        await ProcessTemplateFilesAsync(templateFilesPath, projectDir.FullName, projectName, template);

        return projectDir;
    }

    private async Task ProcessTemplateFilesAsync(string sourcePath, string targetPath, string projectName, TemplateInfo template)
    {
        foreach (var file in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourcePath, file);
            var processedPath = ProcessTemplateString(relativePath, projectName, template);
            var targetFilePath = Path.Combine(targetPath, processedPath);

            // Create target directory if it doesn't exist
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath));

            // Process file content if it's a template
            if (file.EndsWith(".tpl"))
            {
                var content = await File.ReadAllTextAsync(file);
                var processedContent = ProcessTemplateString(content, projectName, template);
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
        foreach (var (key, value) in template.Variables)
        {
            if (!variables.ContainsKey(key))
            {
                variables[key] = value;
            }
        }

        // Process all template variables in one pass
        var result = input;
        bool madeChange;
        int maxIterations = 10; // Prevent infinite loops

        do
        {
            madeChange = false;
            var currentResult = result;

            foreach (var (key, value) in variables)
            {
                var placeholder = $"{{{{ {key} }}}}";
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
            L.LogWarn($"Template processing reached max iterations for input: {input}");
        }

        return result;
    }

    private static string MakeSafeName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "GhostApp";

        // Convert to PascalCase and remove invalid characters
        var words = name.Split(new[] { ' ', '-', '_', '.', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

        var safeName = string.Join("", words.Select(word =>
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
}