using Ghost.Infrastructure.Orchestration.Channels;
using Ghost.Infrastructure.Storage;
using ProcessMetrics = Ghost.Infrastructure.Monitoring.ProcessMetrics;

namespace Ghost2.Infrastructure.Data;

public interface IDataAPI
{
    Task<T?> GetDataAsync<T>(string key);
    Task SetDataAsync<T>(string key, T value);
    Task DeleteDataAsync(string key);
    Task<bool> ExistsAsync(string key);
    Task<IEnumerable<string>> GetKeysByPatternAsync(string pattern);
    Task<ProcessMetrics?> GetProcessMetricsAsync(string processId);
    Task<IEnumerable<ProcessState>> GetProcessHistoryAsync(string processId, DateTime from, DateTime to);
}

public class DataAPI : IDataAPI
{
    private readonly IStorageRouter _storage;
    private readonly IRedisClient _cache;
    private readonly TimeSpan _defaultCacheExpiry = TimeSpan.FromMinutes(5);

    public DataAPI(IStorageRouter storage, IRedisClient cache)
    {
        _storage = storage;
        _cache = cache;
    }

    public async Task<T?> GetDataAsync<T>(string key)
    {
        // Try cache first
        var cached = await _cache.GetAsync<T>(key);
        if (cached != null)
            return cached;

        // If not in cache, get from storage
        var data = await _storage.ReadAsync<T>(key);
        if (data != null)
        {
            // Update cache
            await _cache.SetAsync(key, data, _defaultCacheExpiry);
        }

        return data;
    }

    public async Task SetDataAsync<T>(string key, T value)
    {
        // Write to storage
        await _storage.WriteAsync(key, value);

        // Update cache
        await _cache.SetAsync(key, value, _defaultCacheExpiry);
    }

    public async Task DeleteDataAsync(string key)
    {
        await _storage.DeleteAsync(key);
        await _cache.DeleteAsync(key);
    }

    public async Task<bool> ExistsAsync(string key)
    {
        // Check cache first
        if (await _cache.ExistsAsync(key))
            return true;

        // If not in cache, check storage
        return await _storage.ExistsAsync(key);
    }

    public async Task<IEnumerable<string>> GetKeysByPatternAsync(string pattern)
    {
        // This would depend on your storage implementation
        // For Redis, you could use SCAN with pattern matching
        // For PostgreSQL, you could use LIKE patterns
        throw new NotImplementedException("Implement based on your storage backend");
    }

    public async Task<ProcessMetrics?> GetProcessMetricsAsync(string processId)
    {
        var key = $"metrics:{processId}:latest";
        return await GetDataAsync<ProcessMetrics>(key);
    }

    public async Task<IEnumerable<ProcessState>> GetProcessHistoryAsync(
        string processId,
        DateTime from,
        DateTime to)
    {
        // This would typically involve querying a time-series database
        // or a specially structured table in your storage system
        // Here's a basic implementation using our storage layer

        var key = $"history:{processId}";
        var history = await GetDataAsync<List<ProcessState>>(key);

        if (history == null)
            return Enumerable.Empty<ProcessState>();

        return history
            .Where(state => state.Properties.TryGetValue("timestamp", out var ts) &&
                           DateTime.Parse(ts) >= from &&
                           DateTime.Parse(ts) <= to)
            .OrderBy(state => state.Properties["timestamp"]);
    }

    // Helper method to format timestamp consistently
    private string FormatTimestamp(DateTime timestamp)
    {
        return timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
    }

    // Helper method to parse timestamp consistently
    private DateTime ParseTimestamp(string timestamp)
    {
        return DateTime.Parse(timestamp);
    }
}
