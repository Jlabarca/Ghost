using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ghost.Core.Data.Implementations
{
  /// <summary>
  /// In-memory implementation of the cache interface.
  /// Useful for development, testing, and as a local L1 cache.
  /// </summary>
  public class MemoryCache : ICache
  {
    private class CacheItem
    {
      public object? Value { get; set; }
      public DateTime? Expiry { get; set; }
      public DateTime LastAccessed { get; set; }
    }

    private readonly ConcurrentDictionary<string, CacheItem> _cache = new();
    private readonly ILogger<MemoryCache> _logger;
    private readonly TimeSpan _defaultExpiry = TimeSpan.FromHours(1);
    private readonly TimeSpan _defaultSlidingExpiry = TimeSpan.FromMinutes(20);
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <summary>
    /// Gets the name of this cache provider.
    /// </summary>
    public string Name => "Memory";

    /// <summary>
    /// Indicates whether this is a distributed cache.
    /// </summary>
    public bool IsDistributed => false;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryCache"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public MemoryCache(ILogger<MemoryCache> logger)
    {
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));

      // Start cleanup timer to remove expired items every 5 minutes
      _cleanupTimer = new Timer(CleanupExpiredItems, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      if (_cache.TryGetValue(key, out var item))
      {
        if (IsExpired(item))
        {
          _cache.TryRemove(key, out _);
          return Task.FromResult<T?>(default);
        }

        // Update last accessed time for sliding expiration
        item.LastAccessed = DateTime.UtcNow;

        if (item.Value is T value)
        {
          return Task.FromResult<T?>(value);
        }
      }

      return Task.FromResult<T?>(default);
    }

    /// <inheritdoc />
    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      var expiryTime = expiry.HasValue && expiry.Value > TimeSpan.Zero
          ? DateTime.UtcNow.Add(expiry.Value)
          : DateTime.UtcNow.Add(_defaultExpiry);

      var item = new CacheItem
      {
          Value = value,
          Expiry = expiryTime,
          LastAccessed = DateTime.UtcNow
      };

      _cache[key] = item;
      return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
      ThrowIfDisposed();
      return Task.FromResult(_cache.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      if (_cache.TryGetValue(key, out var item))
      {
        if (IsExpired(item))
        {
          _cache.TryRemove(key, out _);
          return Task.FromResult(false);
        }

        return Task.FromResult(true);
      }

      return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<IDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      var result = new Dictionary<string, T?>();

      foreach (var key in keys)
      {
        if (_cache.TryGetValue(key, out var item))
        {
          if (IsExpired(item))
          {
            _cache.TryRemove(key, out _);
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
    public Task<bool> SetManyAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      var expiryTime = expiry.HasValue && expiry.Value > TimeSpan.Zero
          ? DateTime.UtcNow.Add(expiry.Value)
          : DateTime.UtcNow.Add(_defaultExpiry);

      foreach (var (key, value) in items)
      {
        var item = new CacheItem
        {
            Value = value,
            Expiry = expiryTime,
            LastAccessed = DateTime.UtcNow
        };

        _cache[key] = item;
      }

      return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<int> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      var count = 0;

      foreach (var key in keys)
      {
        if (_cache.TryRemove(key, out _))
        {
          count++;
        }
      }

      return Task.FromResult(count);
    }

    /// <inheritdoc />
    public Task<bool> UpdateExpiryAsync(string key, TimeSpan expiry, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      if (_cache.TryGetValue(key, out var item))
      {
        if (IsExpired(item))
        {
          _cache.TryRemove(key, out _);
          return Task.FromResult(false);
        }

        item.Expiry = DateTime.UtcNow.Add(expiry);
        return Task.FromResult(true);
      }

      return Task.FromResult(false);
    }

    /// <inheritdoc />
    public Task<bool> ClearAsync(CancellationToken ct = default)
    {
      ThrowIfDisposed();

      _cache.Clear();
      return Task.FromResult(true);
    }

    /// <summary>
    /// Checks if a cache item has expired.
    /// </summary>
    private bool IsExpired(CacheItem item)
    {
      if (item.Expiry.HasValue && item.Expiry.Value <= DateTime.UtcNow)
      {
        return true;
      }

      // Check sliding expiration - if item hasn't been accessed in _defaultSlidingExpiry
      if (DateTime.UtcNow - item.LastAccessed > _defaultSlidingExpiry)
      {
        return true;
      }

      return false;
    }

    /// <summary>
    /// Removes expired items from the cache.
    /// </summary>
    private void CleanupExpiredItems(object? state)
    {
      try
      {
        if (_disposed) return;

        var keysToRemove = _cache
            .Where(kvp => IsExpired(kvp.Value))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
          _cache.TryRemove(key, out _);
        }

        _logger.LogTrace("Cleaned up {Count} expired items from memory cache", keysToRemove.Count);
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Error cleaning up expired items from memory cache");
      }
    }

    /// <summary>
    /// Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
      if (_disposed)
        throw new ObjectDisposedException(nameof(MemoryCache));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
      if (_disposed) return;

      _disposed = true;

      await _cleanupTimer.DisposeAsync();
      _cache.Clear();
    }
  }
}
