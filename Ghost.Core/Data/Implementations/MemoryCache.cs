using System.Collections.Concurrent;
using Ghost.Data;
using Ghost.Logging;
using Microsoft.Extensions.Logging;
namespace Ghost;

/// <summary>
///     In-memory implementation of the cache interface.
///     Useful for development, testing, and as a local L1 cache.
/// </summary>
public class MemoryCache : ICache
{

    private readonly ConcurrentDictionary<string, CacheItem> cache = new ConcurrentDictionary<string, CacheItem>();
    private readonly Timer cleanupTimer;
    private readonly TimeSpan defaultExpiry = TimeSpan.FromHours(1);
    private readonly TimeSpan defaultSlidingExpiry = TimeSpan.FromMinutes(20);
    private bool disposed;
    private IGhostLogger? logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MemoryCache" /> class.
    /// </summary>
    /// <param name="logger">The logger. Can be null during bootstrap.</param>
    public MemoryCache(IGhostLogger? logger)
    {
        this.logger = logger;

        // Start cleanup timer to remove expired items every 5 minutes
        cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    ///     Gets the name of this cache provider.
    /// </summary>
    public string Name => "Memory";

    /// <summary>
    ///     Indicates whether this is a distributed cache.
    /// </summary>
    public bool IsDistributed => false;

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        if (cache.TryGetValue(key, out CacheItem? item))
        {
            if (IsExpired(item))
            {
                cache.TryRemove(key, out _);
                return Task.FromResult<T?>(default(T?));
            }

            // Update last accessed time for sliding expiration
            item.LastAccessed = DateTime.UtcNow;

            if (item.Value is T value)
            {
                return Task.FromResult<T?>(value);
            }
        }

        return Task.FromResult<T?>(default(T?));
    }

    /// <inheritdoc />
    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        DateTime expiryTime = expiry.HasValue && expiry.Value > TimeSpan.Zero
                ? DateTime.UtcNow.Add(expiry.Value)
                : DateTime.UtcNow.Add(defaultExpiry);

        CacheItem item = new CacheItem
        {
                Value = value,
                Expiry = expiryTime,
                LastAccessed = DateTime.UtcNow
        };

        cache[key] = item;
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return Task.FromResult(cache.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        if (cache.TryGetValue(key, out CacheItem? item))
        {
            if (IsExpired(item))
            {
                cache.TryRemove(key, out _);
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<IDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        var result = new Dictionary<string, T?>();

        foreach (string key in keys)
        {
            if (cache.TryGetValue(key, out CacheItem? item))
            {
                if (IsExpired(item))
                {
                    cache.TryRemove(key, out _);
                    continue;
                }

                // Update last accessed time for sliding expiration
                item.LastAccessed = DateTime.UtcNow;

                if (item.Value is T value)
                {
                    result[key] = value;
                }
            }
        }

        return Task.FromResult<IDictionary<string, T?>>(result);
    }

    /// <inheritdoc />
    public Task<bool> SetManyAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        DateTime expiryTime = expiry.HasValue && expiry.Value > TimeSpan.Zero
                ? DateTime.UtcNow.Add(expiry.Value)
                : DateTime.UtcNow.Add(defaultExpiry);

        foreach ((string key, T value) in items)
        {
            CacheItem item = new CacheItem
            {
                    Value = value,
                    Expiry = expiryTime,
                    LastAccessed = DateTime.UtcNow
            };

            cache[key] = item;
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<int> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        int count = 0;

        foreach (string key in keys)
        {
            if (cache.TryRemove(key, out _))
            {
                count++;
            }
        }

        return Task.FromResult(count);
    }

    /// <inheritdoc />
    public Task<bool> UpdateExpiryAsync(string key, TimeSpan expiry, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        if (cache.TryGetValue(key, out CacheItem? item))
        {
            if (IsExpired(item))
            {
                cache.TryRemove(key, out _);
                return Task.FromResult(false);
            }

            item.Expiry = DateTime.UtcNow.Add(expiry);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<bool> ClearAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        cache.Clear();
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        await cleanupTimer.DisposeAsync();
        cache.Clear();
    }

    /// <summary>
    ///     Sets or updates the logger for this cache instance.
    ///     This is useful during bootstrap scenarios where the cache is created before the logger.
    /// </summary>
    /// <param name="logger">The logger to use.</param>
    public void SetLogger(IGhostLogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    ///     Checks if a cache item has expired.
    /// </summary>
    private bool IsExpired(CacheItem item)
    {
        if (item.Expiry.HasValue && item.Expiry.Value <= DateTime.UtcNow)
        {
            return true;
        }

        // Check sliding expiration - if item hasn't been accessed in _defaultSlidingExpiry
        if (DateTime.UtcNow - item.LastAccessed > defaultSlidingExpiry)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Removes expired items from the cache.
    /// </summary>
    private void CleanupExpiredItems(object? state)
    {
        try
        {
            if (disposed)
            {
                return;
            }

            var keysToRemove = cache
                    .Where(kvp => IsExpired(kvp.Value))
                    .Select(kvp => kvp.Key)
                    .ToList();

            foreach (string? key in keysToRemove)
            {
                cache.TryRemove(key, out _);
            }

            // Only log if logger is available
            if (logger != null)
            {
                logger.LogTrace("Cleaned up {Count} expired items from memory cache", keysToRemove.Count);
            }
        }
        catch (Exception ex)
        {
            // Only log if logger is available
            if (logger != null)
            {
                logger.LogError(ex, "Error cleaning up expired items from memory cache");
            }
            else
            {
                // Fallback to console if no logger available
                Console.WriteLine($"Error cleaning up expired items from memory cache: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(MemoryCache));
        }
    }
    private class CacheItem
    {
        public object? Value { get; set; }
        public DateTime? Expiry { get; set; }
        public DateTime LastAccessed { get; set; }
    }
}
