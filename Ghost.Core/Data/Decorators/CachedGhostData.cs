using System.Collections.Concurrent;
using Ghost.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ghost.Core.Data;

/// <summary>
/// Decorator that adds multi-level caching to any IGhostData implementation.
/// Provides L1 (memory) cache in front of the decorated IGhostData implementation.
/// </summary>
public class CachedGhostData : IGhostData
{
  private readonly IGhostData _inner;
  private readonly ICache _memoryCache;
  private readonly IOptions<CachingConfiguration> _config;
  private readonly ILogger<CachedGhostData> _logger;
  private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of the <see cref="CachedGhostData"/> class.
  /// </summary>
  /// <param name="inner">The decorated IGhostData implementation.</param>
  /// <param name="memoryCache">The memory cache for L1 caching.</param>
  /// <param name="config">The caching configuration.</param>
  /// <param name="logger">The logger.</param>
  public CachedGhostData(
      IGhostData inner,
      ICache memoryCache,
      IOptions<CachingConfiguration> config,
      ILogger<CachedGhostData> logger)
  {
    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
    _config = config ?? throw new ArgumentNullException(nameof(config));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  public IDatabaseClient GetDatabaseClient() => _inner.GetDatabaseClient();

        #region Key-Value Operations

  /// <inheritdoc />
  public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    // Check L1 cache first
    if (_config.Value.UseL1Cache)
    {
      T? cachedValue = default;
      T? value = await _memoryCache.GetAsync<T>(GetCacheKey(key, typeof(T)), ct);
      _logger.LogTrace("L1 cache hit for key: {Key} (value: {Value})", key, value);
      return value;
    }

    // Get lock for this key to prevent cache stampede
    var keyLock = GetKeyLock(key);

    try
    {
      await keyLock.WaitAsync(ct);

      // Double-check L1 cache after acquiring lock
      if (_config.Value.UseL1Cache)
      {
        T? cachedValue = default;
        T? value = await _memoryCache.GetAsync<T>(GetCacheKey(key, typeof(T)), ct);
        _logger.LogTrace("L1 cache hit for key: {Key} (after lock) (value: {Value})", key, value);
        return value;
      }

      // Get from inner (L2 cache or database)
      var fetchedValue = await _inner.GetAsync<T>(key, ct);

      // Store in L1 cache if enabled
      if (fetchedValue != null && _config.Value.UseL1Cache)
      {
        await _memoryCache.SetAsync(
            GetCacheKey(key, typeof(T)),
            fetchedValue,
            _config.Value.DefaultL1Expiration,
            ct);

        _logger.LogTrace("Added to L1 cache: {Key}", key);
      }

      return fetchedValue;
    }
    finally
    {
      keyLock.Release();
    }
  }

  /// <inheritdoc />
  public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    // Set in L1 cache if enabled
    if (_config.Value.UseL1Cache)
    {
      var l1Expiry = expiry ?? _config.Value.DefaultL1Expiration;
      if (l1Expiry > TimeSpan.Zero)
      {
        await _memoryCache.SetAsync(
            GetCacheKey(key, typeof(T)),
            value,
            l1Expiry,
            ct);

        _logger.LogTrace("Set in L1 cache: {Key}", key);
      }
    }

    // Set in inner (L2 cache or database)
    await _inner.SetAsync(key, value, expiry, ct);
  }

  /// <inheritdoc />
  public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    // Remove from L1 cache (all types)
    if (_config.Value.UseL1Cache)
    {
      await RemoveFromL1CacheAsync(key, ct);
      _logger.LogTrace("Removed from L1 cache: {Key}", key);
    }

    // Delete from inner (L2 cache or database)
    return await _inner.DeleteAsync(key, ct);
  }

  /// <inheritdoc />
  public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    // Check L1 cache first (we can't check specific types here, so just pass through)
    return await _inner.ExistsAsync(key, ct);
  }

        #endregion

        #region Batch Key-Value Operations

  /// <inheritdoc />
  public async Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    var result = new Dictionary<string, T?>();
    var keysToFetch = new List<string>();

    // Check L1 cache first for each key
    if (_config.Value.UseL1Cache)
    {
      foreach (var key in keys)
      {
        var cacheKey = GetCacheKey(key, typeof(T));
        var cachedValue = await _memoryCache.GetAsync<T>(cacheKey, ct);

        if (cachedValue != null)
        {
          _logger.LogTrace("L1 cache hit for key in batch: {Key}", key);
          result[key] = cachedValue;
        } else
        {
          keysToFetch.Add(key);
        }
      }
    } else
    {
      keysToFetch.AddRange(keys);
    }

    // If we found all keys in L1 cache, return early
    if (keysToFetch.Count == 0)
      return result;

    // Get remaining keys from inner (L2 cache or database)
    var innerResults = await _inner.GetBatchAsync<T>(keysToFetch, ct);

    // Store in L1 cache and add to result
    foreach (var (key, value) in innerResults)
    {
      result[key] = value;

      if (value != null && _config.Value.UseL1Cache)
      {
        await _memoryCache.SetAsync(
            GetCacheKey(key, typeof(T)),
            value,
            _config.Value.DefaultL1Expiration,
            ct);

        _logger.LogTrace("Added to L1 cache from batch: {Key}", key);
      }
    }

    return result;
  }

  /// <inheritdoc />
  public async Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    // Set in L1 cache if enabled
    if (_config.Value.UseL1Cache)
    {
      var l1Expiry = expiry ?? _config.Value.DefaultL1Expiration;

      if (l1Expiry > TimeSpan.Zero)
      {
        foreach (var (key, value) in items)
        {
          await _memoryCache.SetAsync(
              GetCacheKey(key, typeof(T)),
              value,
              l1Expiry,
              ct);

          _logger.LogTrace("Set in L1 cache from batch: {Key}", key);
        }
      }
    }

    // Set in inner (L2 cache or database)
    await _inner.SetBatchAsync(items, expiry, ct);
  }

  /// <inheritdoc />
  public async Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    // Remove from L1 cache (all types)
    if (_config.Value.UseL1Cache)
    {
      foreach (var key in keys)
      {
        await RemoveFromL1CacheAsync(key, ct);
        _logger.LogTrace("Removed from L1 cache in batch: {Key}", key);
      }
    }

    // Delete from inner (L2 cache or database)
    return await _inner.DeleteBatchAsync(keys, ct);
  }

        #endregion

        #region SQL Operations

  /// <inheritdoc />
  public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    // SQL operations don't use L1 cache by default, but we could add query-result caching here
    // based on a hash of the SQL and parameters if needed
    return await _inner.QuerySingleAsync<T>(sql, param, ct);
  }

  /// <inheritdoc />
  public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
  {
    ThrowIfDisposed();
    return await _inner.QueryAsync<T>(sql, param, ct);
  }

  /// <inheritdoc />
  public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
  {
    ThrowIfDisposed();
    return await _inner.ExecuteAsync(sql, param, ct);
  }

  /// <inheritdoc />
  public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default)
  {
    ThrowIfDisposed();
    return await _inner.ExecuteBatchAsync(commands, ct);
  }

        #endregion

        #region Transaction Support

  /// <inheritdoc />
  public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default)
  {
    ThrowIfDisposed();
    return await _inner.BeginTransactionAsync(ct);
  }

        #endregion

        #region Schema Operations

  /// <inheritdoc />
  public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
  {
    ThrowIfDisposed();
    return await _inner.TableExistsAsync(tableName, ct);
  }

  /// <inheritdoc />
  public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default)
  {
    ThrowIfDisposed();
    return await _inner.GetTableNamesAsync(ct);
  }

        #endregion

        #region Helper Methods

  /// <summary>
  /// Gets the cache key for a specific key and type.
  /// </summary>
  /// <param name="key">The key.</param>
  /// <param name="type">The type.</param>
  /// <returns>The cache key.</returns>
  private static string GetCacheKey(string key, Type type)
  {
    return $"GhostData:{type.Name}:{key}";
  }

  /// <summary>
  /// Gets or creates a lock for a specific key.
  /// </summary>
  /// <param name="key">The key.</param>
  /// <returns>The lock for the key.</returns>
  private SemaphoreSlim GetKeyLock(string key)
  {
    return _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
  }

  /// <summary>
  /// Removes all type variations of a key from the L1 cache.
  /// </summary>
  /// <param name="key">The key to remove.</param>
  /// <param name="ct">Cancellation token.</param>
  private async Task RemoveFromL1CacheAsync(string key, CancellationToken ct)
  {
    // We can't easily enumerate all cached types for this key, but we
    // can remove it from common types cache. In the future, we could
    // maintain a registry of type keys.
    await _memoryCache.DeleteAsync(GetCacheKey(key, typeof(string)), ct);
    await _memoryCache.DeleteAsync(GetCacheKey(key, typeof(int)), ct);
    await _memoryCache.DeleteAsync(GetCacheKey(key, typeof(long)), ct);
    await _memoryCache.DeleteAsync(GetCacheKey(key, typeof(bool)), ct);
    await _memoryCache.DeleteAsync(GetCacheKey(key, typeof(DateTime)), ct);
    await _memoryCache.DeleteAsync(GetCacheKey(key, typeof(object)), ct);
    await _memoryCache.DeleteAsync(GetCacheKey(key, typeof(Dictionary<string, object>)), ct);
  }

  /// <summary>
  /// Throws if this object has been disposed.
  /// </summary>
  private void ThrowIfDisposed()
  {
    if (_disposed)
      throw new ObjectDisposedException(nameof(CachedGhostData));
  }

        #endregion

        #region IAsyncDisposable

  /// <inheritdoc />
  public async ValueTask DisposeAsync()
  {
    if (_disposed)
      return;

    _disposed = true;

    // Dispose inner
    await _inner.DisposeAsync();

    // Dispose all locks
    foreach (var lockObj in _locks.Values)
    {
      lockObj.Dispose();
    }

    _locks.Clear();
  }

        #endregion
}
