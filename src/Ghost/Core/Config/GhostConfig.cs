using Ghost2.Infrastructure;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Ghost.Core.Config;

/// <summary>
/// Manages application configuration using YAML files
/// Provides type-safe access to configuration values with change tracking
/// </summary>
public interface IGhostConfig : IAsyncDisposable
{
    Task<T> GetAsync<T>(string key, T defaultValue = default);
    Task SetAsync<T>(string key, T value);
    Task DeleteAsync(string key);
    Task<bool> ExistsAsync(string key);
    Task<IEnumerable<string>> GetKeysAsync(string prefix = "");
    event EventHandler<ConfigChangedEventArgs> ConfigChanged;
}

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

public class GhostConfig : IGhostConfig
{
    private readonly string _configPath;
    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, object> _cache;
    private readonly FileSystemWatcher _watcher;
    private bool _disposed;

    public event EventHandler<ConfigChangedEventArgs> ConfigChanged;

    public GhostConfig(GhostOptions options, ILogger<GhostConfig> logger)
    {
        _configPath = Path.GetFullPath(Path.Combine(
            options.DataDirectory ?? "config",
            ".ghost.yaml"
        ));

        _logger = logger;
        _cache = new Dictionary<string, object>();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        // Ensure config directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath));

        // Set up file watcher
        _watcher = new FileSystemWatcher
        {
            Path = Path.GetDirectoryName(_configPath),
            Filter = Path.GetFileName(_configPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
        };

        _watcher.Changed += async (s, e) => await OnConfigFileChanged();
        _watcher.EnableRaisingEvents = true;
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
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert config value for key: {Key}", key);
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
            _logger.LogError(ex, "Failed to load config file: {Path}", _configPath);
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
            _logger.LogError(ex, "Failed to save config file: {Path}", _configPath);
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
            _logger.LogInformation("Configuration file changed, cache cleared");
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
            _logger.LogError(ex, "Error in config change handler");
        }
    }

    private static object GetValueFromPath(Dictionary<object, object> config, string path)
    {
        var current = config;
        var parts = path.Split(':');

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out var next) ||
                !(next is Dictionary<object, object> dict))
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
                !(next is Dictionary<object, object> dict))
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

            _watcher.Dispose();
            _lock.Dispose();
            _cache.Clear();
        }
        finally
        {
            _lock.Release();
        }
    }
}
