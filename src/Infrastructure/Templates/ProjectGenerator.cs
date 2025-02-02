using Microsoft.Extensions.Logging;
namespace Ghost.Infrastructure.Templates;

/// <summary>
/// Handles the actual project generation from templates
/// </summary>
public class ProjectGenerator
{
  private readonly TemplateEngine _engine;
  private readonly ILogger<ProjectGenerator> _logger;

  public ProjectGenerator(TemplateEngine engine, ILogger<ProjectGenerator> logger)
  {
    _engine = engine;
    _logger = logger;
  }

  public async Task<string> GenerateProjectAsync(
      string templateName,
      string projectName,
      string outputPath,
      Dictionary<string, object> variables = null)
  {
    var template = await _engine.LoadTemplateAsync(templateName);
    var projectPath = Path.Combine(outputPath, projectName);

    // Prepare template model
    var model = template.GetTemplateModel(variables);
    model["project_name"] = projectName;
    model["safe_name"] = MakeSafeName(projectName);
    model["created_date"] = DateTime.Now.ToString("yyyy-MM-dd");
    model["created_year"] = DateTime.Now.Year.ToString();

    // Create project directory
    Directory.CreateDirectory(projectPath);

    // Process each template file
    foreach (var file in template.Files)
    {
      try
      {
        var targetPath = Path.Combine(
            projectPath,
            ProcessPath(file.Target, model));

        // Create target directory if needed
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

        if (file.IsTemplate)
        {
          // Read and render template
          var templateContent = await File.ReadAllTextAsync(file.Source);
          var renderedContent = await _engine.RenderTemplateFileAsync(
              templateContent,
              model);

          // Write rendered content
          await File.WriteAllTextAsync(targetPath, renderedContent);
        }
        else
        {
          // Copy non-template file directly
          File.Copy(file.Source, targetPath, true);
        }

        _logger.LogInformation(
            "Generated file: {File}",
            Path.GetRelativePath(projectPath, targetPath));
      }
      catch (Exception ex)
      {
        _logger.LogError(
            ex,
            "Failed to generate file {File}",
            file.Target);
        throw;
      }
    }

    return projectPath;
  }

  private string ProcessPath(string path, Dictionary<string, object> model)
  {
    return path.Replace(
        "{{ project_name }}",
        model["project_name"].ToString(),
        StringComparison.OrdinalIgnoreCase);
  }

  private string MakeSafeName(string name)
  {
    return new string(name
        .Where(c => char.IsLetterOrDigit(c) || c == '_')
        .ToArray());
  }
}
