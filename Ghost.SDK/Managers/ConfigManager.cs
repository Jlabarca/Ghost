using Ghost.Core.Config;
using Ghost.Core.Modules;
namespace Ghost.SDK;

/// <summary>
/// Manages access to configuration
/// </summary>
public class ConfigManager
{
  private readonly GhostConfig _config;

  public ConfigManager(GhostConfig config)
  {
    _config = config;
  }

  /// <summary>
  /// Get the raw configuration
  /// </summary>
  public GhostConfig Raw => _config;

  /// <summary>
  /// Get application configuration
  /// </summary>
  public AppInfo App => _config.App;

  /// <summary>
  /// Get core configuration
  /// </summary>
  public CoreConfig Core => _config.Core;

  /// <summary>
  /// Get a module configuration
  /// </summary>
  public T GetModuleConfig<T>(string moduleName) where T : ModuleConfig
  {
    return _config.GetModuleConfig<T>(moduleName);
  }

  /// <summary>
  /// Check if a module is enabled
  /// </summary>
  public bool HasModule(string moduleName)
  {
    return _config.HasModule(moduleName);
  }

  /// <summary>
  /// Get setting value with optional default
  /// </summary>
  public string GetSetting(string name, string defaultValue = null)
  {
    return _config.Core.Settings.TryGetValue(name, out var value) ? value : defaultValue;
  }

  /// <summary>
  /// Get typed setting value with optional default
  /// </summary>
  public T GetSetting<T>(string name, T defaultValue = default)
  {
    if (!_config.Core.Settings.TryGetValue(name, out var value))
      return defaultValue;

    try
    {
      // Convert string value to requested type
      return (T)Convert.ChangeType(value, typeof(T));
    }
    catch
    {
      return defaultValue;
    }
  }
}
