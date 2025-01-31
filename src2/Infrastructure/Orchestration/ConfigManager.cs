using Ghost2.Infrastructure.Data;
using Ghost2.Infrastructure.Storage;

namespace Ghost2.Infrastructure.Orchestration;

public interface IConfigManager
{
    Task<T?> GetConfigAsync<T>(string key, string? processId = null);
    Task SetConfigAsync<T>(string key, T value, string? processId = null);
    Task DeleteConfigAsync(string key, string? processId = null);
    Task<IEnumerable<string>> GetProcessConfigKeysAsync(string processId);
}

public class ConfigManager : IConfigManager
{
    private readonly IRedisClient _cache;
    private readonly IStorageRouter _storage;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public ConfigManager(IRedisClient cache, IStorageRouter storage)
    {
        _cache = cache;
        _storage = storage;
    }

    public async Task<T?> GetConfigAsync<T>(string key, string? processId = null)
    {
        var fullKey = GetFullKey(key, processId);
        
        // Try cache first
        var cached = await _cache.GetAsync<T>(fullKey);
        if (cached != null)
            return cached;

        // If not in cache, get from storage
        var value = await _storage.ReadAsync<T>(fullKey, StorageType.Database);
        if (value != null)
        {
            // Update cache
            await _cache.SetAsync(fullKey, value, _cacheExpiry);
        }

        return value;
    }

    public async Task SetConfigAsync<T>(string key, T value, string? processId = null)
    {
        var fullKey = GetFullKey(key, processId);
        
        // Save to storage
        await _storage.WriteAsync(fullKey, value, StorageType.Both);
        
        // Update cache
        await _cache.SetAsync(fullKey, value, _cacheExpiry);
    }

    public async Task DeleteConfigAsync(string key, string? processId = null)
    {
        var fullKey = GetFullKey(key, processId);
        await _storage.DeleteAsync(fullKey, StorageType.Both);
    }

    public async Task<IEnumerable<string>> GetProcessConfigKeysAsync(string processId)
    {
        var prefix = $"config:{processId}:";
        // This would need to be implemented based on your storage system
        // Here's a placeholder implementation
        throw new NotImplementedException("GetProcessConfigKeys needs to be implemented based on your storage system");
    }

    private string GetFullKey(string key, string? processId)
    {
        return processId != null ? $"config:{processId}:{key}" : $"config:{key}";
    }
}
