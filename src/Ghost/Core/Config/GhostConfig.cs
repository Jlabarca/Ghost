using Ghost.SDK;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ghost.Core.Config;

/// <summary>
/// Configuration management interface
/// </summary>
public interface IGhostConfig : IAsyncDisposable, IConfigInitializer, IConfigPersister
{
    Task<T> GetAsync<T>(string key, T defaultValue = default);
    Task SetAsync<T>(string key, T value);
    Task DeleteAsync(string key);
    Task<bool> ExistsAsync(string key);
    Task<IEnumerable<string>> GetKeysAsync(string prefix = "");
    event EventHandler<ConfigChangedEventArgs> ConfigChanged;
}

/// <summary>
/// Arguments for configuration change events
/// </summary>
public class ConfigChangedEventArgs : EventArgs
{
    public string Key { get; }
    public object OldValue { get; }
    public object NewValue { get; }

    public ConfigChangedEventArgs(string key, object oldValue, object newValue)
    {
        Key = key;
        OldValue = oldValue;
        NewValue = newValue;
    }
}

/// <summary>
/// YAML-based configuration manager with change tracking and hot reload
/// </summary>
public class GhostConfig : IGhostConfig
{
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, object> _cache;
    private readonly FileSystemWatcher _watcher;
    private bool _disposed;

    public event EventHandler<ConfigChangedEventArgs> ConfigChanged;

    public GhostConfig(GhostOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));

        _configPath = Path.GetFullPath(Path.Combine(
            options.DataDirectory ?? "config",
            ".ghost.yaml"
        ));

        _cache = new Dictionary<string, object>();

        // Initialize YAML serializers
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        // Ensure config directory exists
        var configDir = Path.GetDirectoryName(_configPath);
        if (!Directory.Exists(configDir))
        {
            Directory.CreateDirectory(configDir);
            G.LogInfo("Created configuration directory: {0}", configDir);
        }

        // Set up file watcher
        _watcher = new FileSystemWatcher
        {
            Path = configDir,
            Filter = Path.GetFileName(_configPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += async (s, e) => await OnConfigFileChanged();
        _watcher.EnableRaisingEvents = true;

        G.LogInfo("Configuration initialized at: {0}", _configPath);
    }

    public async Task LoadConfigurationAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _cache.Clear();
            var config = await LoadConfigAsync();
            if (config != null)
            {
                var keys = new List<string>();
                CollectKeys(config, "", keys);
                G.LogInfo("Loaded {0} configuration keys", keys.Count);
            }
        }
        catch (Exception ex)
        {
            G.LogError("Failed to load configuration", ex);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PersistConfigurationAsync()
    {
        if (_cache.Count == 0) return;

        await _lock.WaitAsync();
        try
        {
            var config = await LoadConfigAsync() ?? new Dictionary<object, object>();
            await SaveConfigAsync(config);
            G.LogInfo("Configuration persisted successfully");
        }
        catch (Exception ex)
        {
            G.LogError("Failed to persist configuration", ex);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<T> GetAsync<T>(string key, T defaultValue = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostConfig));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));

        await _lock.WaitAsync();
        try
        {
            // Check cache first
            if (_cache.TryGetValue(key, out var cached) && cached is T typedValue)
            {
                return typedValue;
            }

            // Load and parse config file
            var config = await LoadConfigAsync();

            // Navigate the YAML path
            var value = GetValueFromPath(config, key);
            if (value == null)
            {
                return defaultValue;
            }

            try
            {
                // Convert to requested type
                T result;
                if (typeof(T).IsPrimitive || typeof(T) == typeof(string))
                {
                    result = (T)Convert.ChangeType(value, typeof(T));
                }
                else
                {
                    var serialized = _serializer.Serialize(value);
                    result = _deserializer.Deserialize<T>(serialized);
                }

                // Update cache
                _cache[key] = result;
                G.LogDebug("Retrieved config value for key: {0}", key);
                return result;
            }
            catch (Exception ex)
            {
                G.LogWarn("Failed to convert config value for key: {0} - {1}", key, ex.Message);
                return defaultValue;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetAsync<T>(string key, T value)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostConfig));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));

        await _lock.WaitAsync();
        try
        {
            var oldValue = await GetAsync<T>(key);
            var config = await LoadConfigAsync() ?? new Dictionary<object, object>();

            // Set value in config
            SetValueInPath(config, key.Split(':'), value);

            // Save config
            await SaveConfigAsync(config);

            // Update cache
            _cache[key] = value;

            // Notify change
            OnConfigChanged(key, oldValue, value);
            G.LogDebug("Updated config value for key: {0}", key);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostConfig));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));

        await _lock.WaitAsync();
        try
        {
            var oldValue = _cache.TryGetValue(key, out var value) ? value : null;
            var config = await LoadConfigAsync();
            if (config == null) return;

            // Remove value from config
            RemoveValueFromPath(config, key.Split(':'));

            // Save config
            await SaveConfigAsync(config);

            // Remove from cache
            _cache.Remove(key);

            // Notify change
            OnConfigChanged(key, oldValue, null);
            G.LogDebug("Deleted config key: {0}", key);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostConfig));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));

        await _lock.WaitAsync();
        try
        {
            // Check cache first
            if (_cache.ContainsKey(key)) return true;

            // Check config file
            var config = await LoadConfigAsync();
            return config != null && GetValueFromPath(config, key) != null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IEnumerable<string>> GetKeysAsync(string prefix = "")
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostConfig));

        await _lock.WaitAsync();
        try
        {
            var config = await LoadConfigAsync();
            if (config == null) return Enumerable.Empty<string>();

            var keys = new List<string>();
            CollectKeys(config, "", keys);

            return string.IsNullOrEmpty(prefix)
                ? keys
                : keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<Dictionary<object, object>> LoadConfigAsync()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new Dictionary<object, object>();
            }

            var yaml = await File.ReadAllTextAsync(_configPath);
            return _deserializer.Deserialize<Dictionary<object, object>>(yaml);
        }
        catch (Exception ex)
        {
            G.LogError("Failed to load config file: {0}", ex, _configPath);
            throw new GhostException(
                "Failed to load configuration file",
                ex,
                ErrorCode.ConfigurationError);
        }
    }

    private async Task SaveConfigAsync(Dictionary<object, object> config)
    {
        try
        {
            var yaml = _serializer.Serialize(config);
            await File.WriteAllTextAsync(_configPath, yaml);
        }
        catch (Exception ex)
        {
            G.LogError("Failed to save config file: {0}", ex, _configPath);
            throw new GhostException(
                "Failed to save configuration file",
                ex,
                ErrorCode.ConfigurationError);
        }
    }

    private async Task OnConfigFileChanged()
    {
        await _lock.WaitAsync();
        try
        {
            _cache.Clear();
            G.LogInfo("Configuration file changed, cache cleared");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void OnConfigChanged(string key, object oldValue, object newValue)
    {
        try
        {
            ConfigChanged?.Invoke(this, new ConfigChangedEventArgs(key, oldValue, newValue));
        }
        catch (Exception ex)
        {
            G.LogError("Error in config change handler", ex);
        }
    }

    private static object GetValueFromPath(Dictionary<object, object> config, string path)
    {
        var current = config;
        var parts = path.Split(':');

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var next) ||
                next is not Dictionary<object, object> dict)
            {
                return null;
            }
            current = dict;
        }

        return current.TryGetValue(parts[^1], out var value) ? value : null;
    }

    private static void SetValueInPath(
        Dictionary<object, object> config,
        string[] parts,
        object value)
    {
        var current = config;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var next))
            {
                next = new Dictionary<object, object>();
                current[parts[i]] = next;
            }
            current = next as Dictionary<object, object>;
        }
        current[parts[^1]] = value;
    }

    private static void RemoveValueFromPath(
        Dictionary<object, object> config,
        string[] parts)
    {
        var current = config;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var next) ||
                next is not Dictionary<object, object> dict)
            {
                return;
            }
            current = dict;
        }
        current.Remove(parts[^1]);
    }

    private static void CollectKeys(
        Dictionary<object, object> config,
        string prefix,
        List<string> keys)
    {
        foreach (var kvp in config)
        {
            var key = string.IsNullOrEmpty(prefix) ? kvp.Key.ToString() : $"{prefix}:{kvp.Key}";
            if (kvp.Value is Dictionary<object, object> dict)
            {
                CollectKeys(dict, key, keys);
            }
            else
            {
                keys.Add(key);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;

            await PersistConfigurationAsync();
            _watcher.Dispose();
            _lock.Dispose();
            _cache.Clear();

            G.LogInfo("Configuration disposed successfully");
        }
        finally
        {
            _lock.Release();
        }
    }
}