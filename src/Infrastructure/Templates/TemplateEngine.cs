using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;

namespace Ghost.Infrastructure.Templates;

/// <summary>
/// Core template engine that handles template loading and rendering.
/// Think of this as a translation service that converts template blueprints into actual implementations.
/// </summary>
public class TemplateEngine
{
    private readonly string _templatesPath;
    private readonly Dictionary<string, GhostTemplate> _templates = new();
    private const string LogPrefix = "[TemplateEngine] ";

    public TemplateEngine(string templatesPath)
    {
        _templatesPath = templatesPath;
        LoadTemplates();
    }

    public IEnumerable<GhostTemplate> GetAvailableTemplates()
    {
        return _templates.Values;
    }

    public async Task<string> RenderTemplateFileAsync(
        string templateContent,
        Dictionary<string, object> model)
    {
        try
        {
            Ghost.LogDebug($"{LogPrefix}Starting template rendering");
            var template = Template.Parse(templateContent);
            var context = new TemplateContext
            {
                LoopLimit = 100000,
                RecursiveLimit = 100,
                MemberRenamer = member => member.Name
            };

            var scriptObject = new ScriptObject();
            foreach (var (key, value) in model)
            {
                scriptObject.Add(key, value);
            }

            context.PushGlobal(scriptObject);
            var result = await template.RenderAsync(context);
            Ghost.LogDebug($"{LogPrefix}Template rendered successfully");
            return result;
        }
        catch (Exception ex)
        {
            Ghost.LogError($"{LogPrefix}Failed to render template", ex);
            throw new GhostException(
                "Failed to render template: " + ex.Message,
                ex,
                ErrorCode.TemplateError);
        }
    }

    public async Task<GhostTemplate> LoadTemplateAsync(string name)
    {
        if (!_templates.TryGetValue(name.ToLowerInvariant(), out var template))
        {
            var error = $"Template '{name}' not found. Available templates: {string.Join(", ", _templates.Keys)}";
            Ghost.LogError($"{LogPrefix}{error}");
            throw new GhostException(error, ErrorCode.TemplateNotFound);
        }

        Ghost.LogDebug($"{LogPrefix}Template '{name}' loaded successfully");
        return template;
    }

    private void LoadTemplates()
    {
        try
        {
            Ghost.LogInfo($"{LogPrefix}Starting template discovery in: {_templatesPath}");

            // Ensure templates directory exists
            Directory.CreateDirectory(_templatesPath);

            var templateDirs = Directory.GetDirectories(_templatesPath);
            Ghost.LogDebug($"{LogPrefix}Found {templateDirs.Length} potential template directories");

            foreach (var templateDir in templateDirs)
            {
                LoadTemplateFromDirectory(templateDir);
            }

            Ghost.LogInfo($"{LogPrefix}Template loading completed. {_templates.Count} templates available");
        }
        catch (Exception ex)
        {
            Ghost.LogError($"{LogPrefix}Fatal error during template loading", ex);
            throw new GhostException(
                "Failed to load templates",
                ex,
                ErrorCode.TemplateError);
        }
    }

    private void LoadTemplateFromDirectory(string templateDir)
    {
        var templateFile = Path.Combine(templateDir, "template.json");
        if (!File.Exists(templateFile))
        {
            Ghost.LogDebug($"{LogPrefix}No template.json found in {templateDir}");
            return;
        }

        try
        {
            var templateJson = File.ReadAllText(templateFile);
            var template = System.Text.Json.JsonSerializer
                .Deserialize<GhostTemplate>(templateJson);

            if (template == null)
            {
                Ghost.LogWarn($"{LogPrefix}Failed to deserialize template from {templateFile}");
                return;
            }

            // Load template files
            var filesDir = Path.Combine(templateDir, "files");
            if (Directory.Exists(filesDir))
            {
                LoadTemplateFiles(template, filesDir);
            }
            else
            {
                Ghost.LogWarn($"{LogPrefix}No files directory found for template {template.Name}");
            }

            _templates[template.Name.ToLowerInvariant()] = template;
            Ghost.LogInfo($"{LogPrefix}Loaded template: {template.Name} ({template.Files.Count} files)");
        }
        catch (Exception ex)
        {
            Ghost.LogError(
                $"{LogPrefix}Failed to load template from {templateFile}",
                ex);
        }
    }

    private void LoadTemplateFiles(GhostTemplate template, string filesDir)
    {
        var files = Directory.GetFiles(filesDir, "*.*", SearchOption.AllDirectories);
        Ghost.LogDebug($"{LogPrefix}Found {files.Length} files in template {template.Name}");

        template.Files.AddRange(
            files.Select(f => new TemplateFile
            {
                Source = Path.GetRelativePath(filesDir, f),
                Target = Path.GetRelativePath(filesDir, f)
                    .Replace(".tpl", ""),
                IsTemplate = Path.GetExtension(f) == ".tpl"
            }));
    }
}

public class TemplateFile
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool IsTemplate { get; set; } = true;
}