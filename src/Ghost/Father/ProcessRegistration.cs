namespace Ghost.Father.Models;

public class ProcessRegistration
{
  public string Id { get; set; }
  public string Name { get; set; }
  public string Type { get; set; }
  public string Version { get; set; }
  public string Path { get; set; }
  public Dictionary<string, string> Environment { get; set; }
  public Dictionary<string, string> Configuration { get; set; }
}
