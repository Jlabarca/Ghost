using Ghost.Infrastructure.Monitoring;
using Ghost.Infrastructure.Storage;
using Ghost.Infrastructure.ProcessManagement;
using Ghost.Infrastructure.Orchestration.Channels;
using System.Collections.Concurrent;

namespace Ghost.Infrastructure.Data;

/// <summary>
/// Unified data access interface for the Ghost system.
/// Acts as a high-level facade over our storage infrastructure.
/// </summary>
public interface IDataAPI
{
    // Core Data Operations
    Task<T?> GetDataAsync<T>(string key, StorageType preferredStorage = StorageType.Cache);
    Task SetDataAsync<T>(string key, T value, StorageType storage = StorageType.Both);
    Task DeleteDataAsync(string key, StorageType storage = StorageType.Both);
    Task<bool> ExistsAsync(string key, StorageType storage = StorageType.Cache);
    Task<IEnumerable<string>> GetKeysByPatternAsync(string pattern);

    // Process-Specific Operations
    Task<ProcessInfo?> GetProcessInfoAsync(string processId);
    Task<ProcessMetrics?> GetProcessMetricsAsync(string processId);
    Task<IEnumerable<ProcessState>> GetProcessHistoryAsync(
        string processId,
        DateTime from,
        DateTime to,
        int limit = 100);

    // Batch Operations
    Task<IDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys);
    Task SetManyAsync<T>(IDictionary<string, T> keyValues);

    // Stream Operations
    IAsyncEnumerable<T> StreamDataAsync<T>(string pattern);
}

/// <summary>
/// Implementation of the IDataAPI interface providing unified data access.
/// Think of this as a smart receptionist that knows how to route and handle all data requests.
/// </summary>
public class DataAPI : IDataAPI
{
    private readonly IStorageRouter _storage;
    private readonly IRedisClient _cache;
    private readonly TimeSpan _defaultCacheExpiry = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _keyLocks;

    public DataAPI(
        IStorageRouter storage,
        IRedisClient cache)
    {
        _storage = storage;
        _cache = cache;
        _keyLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
    }

    public async Task<T?> GetDataAsync<T>(string key, StorageType preferredStorage = StorageType.Cache)
    {
        // First try cache if preferred
        if (preferredStorage == StorageType.Cache)
        {
            var cached = await _cache.GetAsync<T>(key);
            if (cached != null)
            {
                return cached;
            }
        }

        // Get from storage
        var data = await _storage.ReadAsync<T>(key);

        // Update cache if we got from storage
        if (data != null && preferredStorage != StorageType.Database)
        {
            await _cache.SetAsync(key, data, _defaultCacheExpiry);
        }

        return data;
    }

    public async Task SetDataAsync<T>(string key, T value, StorageType storage = StorageType.Both)
    {
        // Get or create lock for this key
        var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await keyLock.WaitAsync();
        try
        {
            // Write to storage
            await _storage.WriteAsync(key, value, storage);

            // Update cache if needed
            if (storage == StorageType.Both || storage == StorageType.Cache)
            {
                await _cache.SetAsync(key, value, _defaultCacheExpiry);
            }
        }
        finally
        {
            keyLock.Release();
        }
    }

    public async Task DeleteDataAsync(string key, StorageType storage = StorageType.Both)
    {
        var keyLock = _keyLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await keyLock.WaitAsync();
        try
        {
            await _storage.DeleteAsync(key, storage);
        }
        finally
        {
            keyLock.Release();
        }
    }

    public async Task<bool> ExistsAsync(string key, StorageType storage = StorageType.Cache)
    {
        // Check cache first if appropriate
        if (storage == StorageType.Cache || storage == StorageType.Both)
        {
            if (await _cache.ExistsAsync(key))
            {
                return true;
            }
        }

        // Check storage if needed
        if (storage == StorageType.Database || storage == StorageType.Both)
        {
            return await _storage.ExistsAsync(key);
        }

        return false;
    }

    public async Task<ProcessInfo?> GetProcessInfoAsync(string processId)
    {
        return await GetDataAsync<ProcessInfo>($"process:{processId}:info");
    }

    public async Task<ProcessMetrics?> GetProcessMetricsAsync(string processId)
    {
        return await GetDataAsync<ProcessMetrics>($"process:{processId}:metrics");
    }

    public async Task<IEnumerable<ProcessState>> GetProcessHistoryAsync(
        string processId,
        DateTime from,
        DateTime to,
        int limit = 100)
    {
        var history = new List<ProcessState>();
        var pattern = $"process:{processId}:state:*";

        await foreach (var state in StreamDataAsync<ProcessState>(pattern))
        {
            if (state.Properties.TryGetValue("timestamp", out var tsStr) &&
                DateTime.TryParse(tsStr, out var timestamp) &&
                timestamp >= from && timestamp <= to)
            {
                history.Add(state);
                if (history.Count >= limit) break;
            }
        }

        return history;
    }

    public async Task<IEnumerable<string>> GetKeysByPatternAsync(string pattern)
    {
        // This would typically use Redis SCAN or similar
        // For now, we'll return an empty list as a placeholder
        return Array.Empty<string>();
    }

    public async Task<IDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys)
    {
        var results = new ConcurrentDictionary<string, T?>();
        var tasks = keys.Select(async key =>
        {
            var value = await GetDataAsync<T>(key);
            results.TryAdd(key, value);
        });

        await Task.WhenAll(tasks);
        return results;
    }

    public async Task SetManyAsync<T>(IDictionary<string, T> keyValues)
    {
        var tasks = keyValues.Select(kv =>
            SetDataAsync(kv.Key, kv.Value));

        await Task.WhenAll(tasks);
    }

    public async IAsyncEnumerable<T> StreamDataAsync<T>(string pattern)
    {
        var keys = await GetKeysByPatternAsync(pattern);
        foreach (var key in keys)
        {
            var value = await GetDataAsync<T>(key);
            if (value != null)
            {
                yield return value;
            }
        }
    }
}