using System.Text.Json;

namespace Ghost.Services;

public class AliasManager
{
  private readonly string _aliasPath;
  private Dictionary<string, string> _aliases;

  public AliasManager()
  {
    _aliasPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ghost",
        "aliases.json"
    );
    LoadAliases();
  }

  private void LoadAliases()
  {
    if (File.Exists(_aliasPath))
    {
      var json = File.ReadAllText(_aliasPath);
      _aliases = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                 ?? new Dictionary<string, string>();
    }
    else
    {
      _aliases = new Dictionary<string, string>();
    }
  }

  private void SaveAliases()
  {
    var directory = Path.GetDirectoryName(_aliasPath);
    if (!string.IsNullOrEmpty(directory))
    {
      Directory.CreateDirectory(directory);
    }
    var json = JsonSerializer.Serialize(_aliases, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(_aliasPath, json);
  }

  public void CreateAlias(string name, string url)
  {
    _aliases[name] = url;
    SaveAliases();
  }

  public void RemoveAlias(string name)
  {
    if (_aliases.Remove(name))
    {
      SaveAliases();
    }
  }

  public string GetAliasUrl(string name)
  {
    return _aliases.TryGetValue(name, out var url) ? url : null;
  }

  public Dictionary<string, string> GetAllAliases()
  {
    return new Dictionary<string, string>(_aliases);
  }
}
