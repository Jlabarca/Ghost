using Ghost.Infrastructure.Orchestration;

namespace Ghost.SDK.Services;

/// <summary>
/// Manages process configuration settings.
/// Think of this as a "control panel" that lets you adjust settings across the system.
/// </summary>
public interface IConfigClient
{
    /// <summary>
    /// Retrieves a configuration value
    /// </summary>
    Task<T> GetConfigAsync<T>(string key);

    /// <summary>
    /// Sets a configuration value
    /// </summary>
    Task SetConfigAsync<T>(string key, T value);

    /// <summary>
    /// Removes a configuration value
    /// </summary>
    Task DeleteConfigAsync(string key);

    /// <summary>
    /// Lists all configuration keys
    /// </summary>
    Task<IEnumerable<string>> GetConfigKeysAsync();

    /// <summary>
    /// Event raised when configuration changes
    /// </summary>
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

/// <summary>
/// Implements configuration management with caching and change notification.
/// Like a "settings coordinator" that keeps all processes in sync with their configurations.
/// </summary>
public class ConfigClient : IConfigClient
{
    private readonly IConfigManager _configManager;
    private readonly IRedisManager _redisManager;
    private readonly string _processId;
    private readonly IDictionary<string, object> _configCache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public event EventHandler<ConfigChangedEventArgs> ConfigChanged;

    public ConfigClient(IConfigManager configManager, IRedisManager redisManager)
    {
        _configManager = configManager;
        _redisManager = redisManager;
        _processId = Guid.NewGuid().ToString();
        _configCache = new Dictionary<string, object>();

        // Start listening for config changes
        _ = StartConfigChangeListenerAsync();
    }

    public async Task<T> GetConfigAsync<T>(string key)
    {
        await _cacheLock.WaitAsync();
        try
        {
            // Check cache first
            if (_configCache.TryGetValue(key, out var cached) && cached is T typed)
                return typed;

            // Get from manager and cache
            var value = await _configManager.GetConfigAsync<T>(key, _processId);
            if (value != null)
                _configCache[key] = value;

            return value;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task SetConfigAsync<T>(string key, T value)
    {
        await _cacheLock.WaitAsync();
        try
        {
            var oldValue = _configCache.TryGetValue(key, out var existing) ? existing : null;

            await _configManager.SetConfigAsync(key, value, _processId);
            _configCache[key] = value;

            OnConfigChanged(key, oldValue, value);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task DeleteConfigAsync(string key)
    {
        await _cacheLock.WaitAsync();
        try
        {
            var oldValue = _configCache.TryGetValue(key, out var existing) ? existing : null;

            await _configManager.DeleteConfigAsync(key, _processId);
            _configCache.Remove(key);

            OnConfigChanged(key, oldValue, null);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public Task<IEnumerable<string>> GetConfigKeysAsync()
    {
        return _configManager.GetProcessConfigKeysAsync(_processId);
    }

    private async Task StartConfigChangeListenerAsync()
    {
        try
        {
            await foreach (var state in _redisManager.SubscribeToStateAsync(_processId))
            {
                if (state.Status == "config_changed" &&
                    state.Properties.TryGetValue("key", out var key))
                {
                    await _cacheLock.WaitAsync();
                    try
                    {
                        _configCache.Remove(key);
                    }
                    finally
                    {
                        _cacheLock.Release();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log error but don't throw from background task
            Console.Error.WriteLine($"Config change listener error: {ex.Message}");
        }
    }

    protected virtual void OnConfigChanged(string key, object oldValue, object newValue)
    {
        ConfigChanged?.Invoke(this, new ConfigChangedEventArgs(key, oldValue, newValue));
    }
}
