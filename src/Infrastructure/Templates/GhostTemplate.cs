
namespace Ghost.Infrastructure.Templates;

/// <summary>
/// Represents a Ghost project template structure
/// </summary>
public class GhostTemplate
{
  public string Name { get; set; }
  public string Description { get; set; }
  public string Author { get; set; }
  public string Version { get; set; }
  public Dictionary<string, object> Variables { get; set; } = new();
  public List<TemplateFile> Files { get; set; } = new();
  public List<string> Tags { get; set; } = new();

  public Dictionary<string, object> GetTemplateModel(Dictionary<string, object> additionalVars = null)
  {
    var model = new Dictionary<string, object>(Variables);
    if (additionalVars != null)
    {
      foreach (var (key, value) in additionalVars)
      {
        model[key] = value;
      }
    }
    return model;
  }
}
