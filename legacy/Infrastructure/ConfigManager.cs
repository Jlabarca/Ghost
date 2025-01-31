using Ghost.Legacy.Services;
using NLua;
using System.Text;

namespace Ghost.Legacy;

public class ConfigManager : IDisposable
{
    private readonly string _configPath;
    private readonly string _ghostConfigFile;
    private readonly string _globalConfigFile;
    private readonly Lua _lua;
    private Dictionary<string, string> _aliases;
    private Dictionary<string, string> _settings;

    private const string DefaultConfig = """
        -- Ghost Configuration File
        config = {
            -- Settings
            settings = {
                -- Example: githubToken = "your-token"
            },
            -- Aliases
            aliases = {
                -- Example: myapp = "https://github.com/user/app"
            }
        }

        -- Helper function to add an alias
        function addAlias(name, url)
            config.aliases[name] = url
        end

        -- Helper function to remove an alias
        function removeAlias(name)
            config.aliases[name] = nil
        end

        -- Helper function to add a setting
        function addSetting(key, value)
            config.settings[key] = value
        end

        return config
        """;

    public ConfigManager()
    {
        // Store local workspace configs in AppData/Local
        _configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ghost");

        // Local project config file
        _ghostConfigFile = ".ghost";

        // Global config in user's home directory
        _globalConfigFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ghostrc.lua");

        _lua = new Lua();
        _aliases = new Dictionary<string, string>();
        _settings = new Dictionary<string, string>();
        LoadSettings();
    }

    public Dictionary<string, string> GetAllAliases()
    {
        return _aliases.Where(x => x.Value != null)
            .ToDictionary(x => x.Key, x => x.Value);
    }

    public void SaveAlias(string alias, string url)
    {
        _aliases[alias] = url;
        SaveSettings();
    }

    public string GetAlias(string alias)
    {
        return _aliases.TryGetValue(alias, out var url) ? url : null;
    }

    public void SaveSetting(string key, string value)
    {
        _settings[key] = value;
        SaveSettings();
    }

    public string GetSetting(string key)
    {
        // First check local .ghost file
        if (File.Exists(_ghostConfigFile))
        {
            try
            {
                LoadLuaFile(_ghostConfigFile);
                var localConfig = _lua.GetTable("config");
                var localSettings = GetDictionaryFromLuaTable(localConfig["settings"] as LuaTable);
                if (localSettings.TryGetValue(key, out var value))
                {
                    return value;
                }
            }
            catch
            {
                // Ignore local file errors and fall back to global settings
            }
        }

        return _settings.TryGetValue(key, out var globalValue) ? globalValue : null;
    }

    private void LoadSettings()
    {
        // Create default global config if it doesn't exist
        if (!File.Exists(_globalConfigFile))
        {
            var configDir = Path.GetDirectoryName(_globalConfigFile);
            if (!string.IsNullOrEmpty(configDir))
            {
                Directory.CreateDirectory(configDir);
            }
            File.WriteAllText(_globalConfigFile, DefaultConfig);
        }

        try
        {
            LoadLuaFile(_globalConfigFile);
            var config = _lua.GetTable("config");

            // Load settings
            var settingsTable = config["settings"] as LuaTable;
            _settings = GetDictionaryFromLuaTable(settingsTable);

            // Load aliases
            var aliasesTable = config["aliases"] as LuaTable;
            _aliases = GetDictionaryFromLuaTable(aliasesTable);
        }
        catch
        {
            // If config is corrupted, start fresh
            _settings = new Dictionary<string, string>();
            _aliases = new Dictionary<string, string>();
        }
    }

    private void SaveSettings()
    {
        var configBuilder = new StringBuilder();
        configBuilder.AppendLine("config = {");

        // Save settings
        configBuilder.AppendLine("    settings = {");
        foreach (var setting in _settings)
        {
            configBuilder.AppendLine($"        [\"{setting.Key}\"] = \"{setting.Value}\",");
        }
        configBuilder.AppendLine("    },");

        // Save aliases
        configBuilder.AppendLine("    aliases = {");
        foreach (var alias in _aliases.Where(x => x.Value != null))
        {
            configBuilder.AppendLine($"        [\"{alias.Key}\"] = \"{alias.Value}\",");
        }
        configBuilder.AppendLine("    }");
        configBuilder.AppendLine("}");

        // Add helper functions
        configBuilder.AppendLine("""
            function addAlias(name, url)
                config.aliases[name] = url
            end

            function removeAlias(name)
                config.aliases[name] = nil
            end

            function addSetting(key, value)
                config.settings[key] = value
            end

            return config
            """);

        File.WriteAllText(_globalConfigFile, configBuilder.ToString());
    }

    private void LoadLuaFile(string filePath)
    {
        _lua.DoFile(filePath);
    }

    private Dictionary<string, string> GetDictionaryFromLuaTable(LuaTable table)
    {
        var dict = new Dictionary<string, string>();
        if (table == null) return dict;

        foreach (var key in table.Keys)
        {
            var value = table[key]?.ToString();
            if (value != null)
            {
                dict[key.ToString()] = value;
            }
        }
        return dict;
    }

    public void Dispose()
    {
        _lua?.Dispose();
    }
    public WorkspaceSettings GetWorkspaceSettings()
    {
        throw new NotImplementedException(); //TODO: Implement this method
    }
}