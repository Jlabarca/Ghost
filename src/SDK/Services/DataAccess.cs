using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Storage;
namespace Ghost.SDK.Services;

/// <summary>
/// Implements data access with caching and process isolation.
/// Like a "data librarian" that efficiently manages storage and retrieval of information.
/// </summary>
public class DataAccess : IDataAccess
{
    private readonly IDataAPI _dataApi;
    private readonly IRedisClient _cache;
    private readonly string _processId;
    private readonly TimeSpan _defaultCacheExpiry = TimeSpan.FromMinutes(5);

    public DataAccess(IDataAPI dataApi, IRedisClient cache)
    {
        _dataApi = dataApi;
        _cache = cache;
        _processId = Guid.NewGuid().ToString();
    }

    public async Task<T> GetDataAsync<T>(string key)
    {
        var processKey = GetProcessKey(key);

        // Try cache first
        var cached = await _cache.GetAsync<T>(processKey);
        if (cached != null)
            return cached;

        // Get from storage and cache
        var value = await _dataApi.GetDataAsync<T>(processKey);
        if (value != null)
            await _cache.SetAsync(processKey, value, _defaultCacheExpiry);

        return value;
    }

    public async Task SetDataAsync<T>(string key, T value)
    {
        var processKey = GetProcessKey(key);

        await _dataApi.SetDataAsync(processKey, value);
        await _cache.SetAsync(processKey, value, _defaultCacheExpiry);
    }

    public async Task DeleteDataAsync(string key)
    {
        var processKey = GetProcessKey(key);

        await _dataApi.DeleteDataAsync(processKey);
        await _cache.DeleteAsync(processKey);
    }

    public async Task<bool> ExistsAsync(string key)
    {
        var processKey = GetProcessKey(key);

        // Check cache first
        if (await _cache.ExistsAsync(processKey))
            return true;

        return await _dataApi.ExistsAsync(processKey);
    }

    public async Task<IEnumerable<string>> FindKeysAsync(string pattern)
    {
        var processPattern = GetProcessKey(pattern);
        var keys = await _dataApi.GetKeysByPatternAsync(processPattern);

        // Strip process prefix from keys before returning
        return keys.Select(k => k.Substring(_processId.Length + 1));
    }

    private string GetProcessKey(string key) => $"{_processId}:{key}";
}

/// <summary>
/// Manages process data storage and retrieval.
/// Think of this as a "smart filing system" that handles all data operations.
/// </summary>
public interface IDataAccess
{
    /// <summary>
    /// Retrieves data by key
    /// </summary>
    Task<T> GetDataAsync<T>(string key);

    /// <summary>
    /// Stores data with a key
    /// </summary>
    Task SetDataAsync<T>(string key, T value);

    /// <summary>
    /// Removes data by key
    /// </summary>
    Task DeleteDataAsync(string key);

    /// <summary>
    /// Checks if data exists
    /// </summary>
    Task<bool> ExistsAsync(string key);

    /// <summary>
    /// Searches for keys matching a pattern
    /// </summary>
    Task<IEnumerable<string>> FindKeysAsync(string pattern);
}

