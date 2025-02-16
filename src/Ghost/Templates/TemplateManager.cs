using Scriban;
using System.Text;
namespace Ghost.Templates;

public class TemplateManager
{
    private readonly string _templatesPath;
    private readonly Dictionary<string, GhostTemplate> _templates = new();

    public TemplateManager(string templatesPath)
    {
        _templatesPath = templatesPath;
        LoadTemplates();
    }

    private void LoadTemplates()
    {
        foreach (var dir in Directory.GetDirectories(_templatesPath))
        {
            var templateName = Path.GetFileName(dir);
            _templates[templateName] = new GhostTemplate(templateName, dir);
        }
    }

    public async Task<DirectoryInfo> CreateFromTemplateAsync(
        string templateName,
        string projectName,
        string outputPath)
    {
        if (!_templates.TryGetValue(templateName, out var template))
            throw new KeyNotFoundException($"Template {templateName} not found");

        // Create project directory
        var projectDir = new DirectoryInfo(Path.Combine(outputPath, projectName));
        projectDir.Create();

        // Prepare template variables
        var templateVars = new Dictionary<string, object>
        {
            ["project_name"] = projectName,
            ["safe_name"] = MakeSafeName(projectName),
            ["created_at"] = DateTime.UtcNow,
            ["ghost_version"] = typeof(GhostTemplate).Assembly.GetName().Version.ToString()
        };

        // Add template-specific variables
        foreach (var (key, value) in template.Variables)
        {
            templateVars[key] = value;
        }

        // Process template files
        await ProcessTemplateFilesAsync(template, projectDir, templateVars);

        // Install dependencies
        await InstallDependenciesAsync(template, projectDir.FullName);

        return projectDir;
    }

    private async Task ProcessTemplateFilesAsync(
        GhostTemplate template,
        DirectoryInfo targetDir,
        Dictionary<string, object> variables)
    {
        var templateFiles = Directory.GetFiles(
            Path.Combine(template.TemplateRoot, "files"),
            "*.*",
            SearchOption.AllDirectories);

        foreach (var templateFile in templateFiles)
        {
            var relativePath = Path.GetRelativePath(
                Path.Combine(template.TemplateRoot, "files"),
                templateFile);

            // Process file path template
            var pathTemplate = Template.Parse(relativePath);
            var targetPath = pathTemplate.Render(variables);
            targetPath = Path.Combine(targetDir.FullName, targetPath);

            // Create directory if needed
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

            // Process file content
            var content = await File.ReadAllTextAsync(templateFile);
            if (Path.GetExtension(templateFile) == ".tpl")
            {
                var scribanTemplate = Template.Parse(content);
                content = await scribanTemplate.RenderAsync(variables);
                targetPath = targetPath[..^4]; // Remove .tpl extension
            }

            await File.WriteAllTextAsync(targetPath, content);
        }
    }

    private static async Task InstallDependenciesAsync(
        GhostTemplate template,
        string projectPath)
    {
        foreach (var package in template.RequiredPackages)
        {
            await DotNetHelper.AddPackageAsync(
                projectPath,
                package.Key,
                package.Value);
        }
    }

    private static string MakeSafeName(string name)
    {
        return new string(
            name.Select(c => char.IsLetterOrDigit(c) ? c : '_')
                .ToArray());
    }

    public Task<GhostTemplate> GetTemplateAsync(string settingsTemplate)
    {
        if (!_templates.TryGetValue(settingsTemplate, out var template))
            throw new KeyNotFoundException($"Template {settingsTemplate} not found");

        return Task.FromResult(template);
    }
}