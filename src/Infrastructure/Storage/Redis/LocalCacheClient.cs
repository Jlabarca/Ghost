namespace Ghost.Infrastructure.Storage;

// Local cache implementation that persists to disk
public class LocalCacheClient : IRedisClient
{
    private readonly string _cacheDir;
    private readonly Dictionary<string, object> _memoryCache;
    private readonly SemaphoreSlim _lock;

    public LocalCacheClient(string cacheDir)
    {
        _cacheDir = cacheDir;
        _memoryCache = new Dictionary<string, object>();
        _lock = new SemaphoreSlim(1, 1);

        Directory.CreateDirectory(_cacheDir);
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        await _lock.WaitAsync();
        try
        {
            _memoryCache[key] = value;

            // Persist to disk
            var path = Path.Combine(_cacheDir, key);
            var json = System.Text.Json.JsonSerializer.Serialize(value);
            await File.WriteAllTextAsync(path, json);

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<T> GetAsync<T>(string key)
    {
        await _lock.WaitAsync();
        try
        {
            // Check memory cache first
            if (_memoryCache.TryGetValue(key, out var cached))
            {
                return (T)cached;
            }

            // Check disk cache
            var path = Path.Combine(_cacheDir, key);
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                var value = System.Text.Json.JsonSerializer.Deserialize<T>(json);
                _memoryCache[key] = value;
                return value;
            }

            return default;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            _memoryCache.Remove(key);

            var path = Path.Combine(_cacheDir, key);
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        await _lock.WaitAsync();
        try
        {
            return _memoryCache.ContainsKey(key) ||
                   File.Exists(Path.Combine(_cacheDir, key));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> PublishAsync(string channel, string message)
    {
        // Local implementation doesn't support pub/sub
        return 0;
    }

    public async IAsyncEnumerable<string> SubscribeAsync(
        string channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Local implementation doesn't support pub/sub
        yield break;
    }

    public async ValueTask DisposeAsync()
    {
        _lock.Dispose();
    }
}