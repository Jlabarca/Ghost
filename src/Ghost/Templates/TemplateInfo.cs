namespace Ghost.Templates;

internal class TemplateInfo
{
  public string Name { get; set; }
  public string Description { get; set; }
  public string Author { get; set; }
  public string Version { get; set; }
  public Dictionary<string, string> Variables { get; set; }
  public Dictionary<string, string> RequiredPackages { get; set; }
  public string[] Tags { get; set; }
}
