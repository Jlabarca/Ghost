using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Ghost.Core.Storage.Cache;

/// <summary>
/// Local memory cache implementation with persistence support
/// </summary>
public class LocalCache : ICache, IMessageBus
{
  private readonly string _storageDirectory;
  private readonly ConcurrentDictionary<string, object> _cache;
  private readonly ConcurrentDictionary<string, DateTime> _expirations;
  private readonly ILogger<LocalCache> _logger;
  private readonly Timer _cleanupTimer;
  private readonly SemaphoreSlim _lock;
  private bool _disposed;

   private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Channel>> _channels;
    private readonly string _subscriptionDirectory;

    public LocalCache(
        string storageDirectory,
        ILogger<LocalCache> logger,
        TimeSpan? cleanupInterval = null)
    {
        // Previous initialization remains the same
        _channels = new ConcurrentDictionary<string, ConcurrentDictionary<string, Channel>>();
        _subscriptionDirectory = Path.Combine(_storageDirectory, "subscriptions");
        Directory.CreateDirectory(_subscriptionDirectory);
    }

    // Implement IMessageBus methods
    public async Task<long> PublishAsync(string channel, string message, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(channel);

        try
        {
            if (_channels.TryGetValue(channel, out var subscribers))
            {
                var count = 0;
                foreach (var (_, channelQueue) in subscribers)
                {
                    await channelQueue.EnqueueAsync(message, ct);
                    count++;
                }
                return count;
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish message to channel: {Channel}", channel);
            throw new StorageException(
                $"Failed to publish message to channel: {channel}",
                ex,
                StorageErrorCode.OperationFailed);
        }
    }

    public async IAsyncEnumerable<string> SubscribeAsync(
        string channel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(channel);

        var subscriberId = Guid.NewGuid().ToString();
        var channelQueue = new Channel();

        try
        {
            // Add subscriber
            var subscribers = _channels.GetOrAdd(channel, _ => new ConcurrentDictionary<string, Channel>());
            subscribers[subscriberId] = channelQueue;

            // Yield messages until cancelled
            while (!ct.IsCancellationRequested)
            {
                var message = await channelQueue.DequeueAsync(ct);
                if (message != null)
                {
                    yield return message;
                }
            }
        }
        finally
        {
            // Remove subscriber
            if (_channels.TryGetValue(channel, out var subs))
            {
                subs.TryRemove(subscriberId, out _);
                if (!subs.Any())
                {
                    _channels.TryRemove(channel, out _);
                }
            }
        }
    }

    public Task<long> SubscriberCountAsync(string channel, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(channel);

        return Task.FromResult(_channels.TryGetValue(channel, out var subscribers)
            ? (long)subscribers.Count
            : 0);
    }

    public Task UnsubscribeAsync(string channel, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(channel);

        _channels.TryRemove(channel, out _);
        return Task.CompletedTask;
    }

    // Add this helper class for message queuing
    private class Channel
    {
        private readonly Channel<string> _channel = System.Threading.Channels.Channel.CreateUnbounded<string>();

        public async Task EnqueueAsync(string message, CancellationToken ct)
        {
            await _channel.Writer.WriteAsync(message, ct);
        }

        public async Task<string> DequeueAsync(CancellationToken ct)
        {
            try
            {
                return await _channel.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
  public LocalCache(
      string storageDirectory,
      ILogger<LocalCache> logger,
      TimeSpan? cleanupInterval = null)
  {
    _storageDirectory = storageDirectory ?? throw new ArgumentNullException(nameof(storageDirectory));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _cache = new ConcurrentDictionary<string, object>();
    _expirations = new ConcurrentDictionary<string, DateTime>();
    _lock = new SemaphoreSlim(1, 1);

    Directory.CreateDirectory(_storageDirectory);

    // Start cleanup timer
    var interval = cleanupInterval ?? TimeSpan.FromMinutes(5);
    _cleanupTimer = new Timer(
        CleanupCallback,
        null,
        interval,
        interval);
  }

  public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

    try
    {
      var testFile = Path.Combine(_storageDirectory, "test.tmp");
      await File.WriteAllTextAsync(testFile, "test", ct);
      File.Delete(testFile);
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Storage directory is not accessible: {Directory}", _storageDirectory);
      return false;
    }
  }

  public Task<long> GetStorageSizeAsync(CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

    try
    {
      var size = Directory.GetFiles(_storageDirectory, "*", SearchOption.AllDirectories)
          .Sum(f => new FileInfo(f).Length);
      return Task.FromResult(size);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to calculate storage size");
      throw new StorageException(
          "Failed to calculate storage size",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
    ValidateKey(key);

    // Check memory cache first
    if (_cache.TryGetValue(key, out var cached))
    {
      if (cached is CacheEntry<T> entry && !entry.IsExpired)
      {
        return entry.Value;
      }
      // Remove expired entry
      _cache.TryRemove(key, out _);
      _expirations.TryRemove(key, out _);
    }

    // Try loading from disk
    try
    {
      var path = GetFilePath(key);
      if (!File.Exists(path))
      {
        return default;
      }

      await _lock.WaitAsync(ct);
      try
      {
        var json = await File.ReadAllTextAsync(path, ct);
        var entry = JsonSerializer.Deserialize<CacheEntry<T>>(json);

        if (entry == null || entry.IsExpired)
        {
          File.Delete(path);
          return default;
        }

        // Update memory cache
        _cache[key] = entry;
        if (entry.ExpiresAt.HasValue)
        {
          _expirations[key] = entry.ExpiresAt.Value;
        }

        return entry.Value;
      }
      finally
      {
        _lock.Release();
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to get cache entry: {Key}", key);
      throw new StorageException(
          $"Failed to get cache entry: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<bool> SetAsync<T>(
      string key,
      T value,
      TimeSpan? expiry = null,
      CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
    ValidateKey(key);

    try
    {
      var entry = new CacheEntry<T>
      {
          Value = value,
          ExpiresAt = expiry.HasValue ? DateTime.UtcNow + expiry.Value : null
      };

      await _lock.WaitAsync(ct);
      try
      {
        // Update memory cache
        _cache[key] = entry;
        if (entry.ExpiresAt.HasValue)
        {
          _expirations[key] = entry.ExpiresAt.Value;
        }

        // Persist to disk
        var path = GetFilePath(key);
        var json = JsonSerializer.Serialize(entry);
        await File.WriteAllTextAsync(path, json, ct);

        return true;
      }
      finally
      {
        _lock.Release();
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to set cache entry: {Key}", key);
      throw new StorageException(
          $"Failed to set cache entry: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
    ValidateKey(key);

    try
    {
      // Remove from memory
      _cache.TryRemove(key, out _);
      _expirations.TryRemove(key, out _);

      // Remove from disk
      var path = GetFilePath(key);
      if (File.Exists(path))
      {
        await _lock.WaitAsync(ct);
        try
        {
          File.Delete(path);
          return true;
        }
        finally
        {
          _lock.Release();
        }
      }

      return false;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to delete cache entry: {Key}", key);
      throw new StorageException(
          $"Failed to delete cache entry: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
    ValidateKey(key);

    try
    {
      // Check memory first
      if (_cache.ContainsKey(key))
      {
        return Task.FromResult(true);
      }

      // Check disk
      var path = GetFilePath(key);
      return Task.FromResult(File.Exists(path));
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to check cache entry existence: {Key}", key);
      throw new StorageException(
          $"Failed to check cache entry existence: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<long> IncrementAsync(
      string key,
      long value = 1,
      CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
    ValidateKey(key);

    await _lock.WaitAsync(ct);
    try
    {
      var current = await GetAsync<long>(key, ct);
      var newValue = current + value;
      await SetAsync(key, newValue, ct: ct);
      return newValue;
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task<bool> ExpireAsync(
      string key,
      TimeSpan expiry,
      CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
    ValidateKey(key);

    try
    {
      if (_cache.TryGetValue(key, out var cached))
      {
        var expiresAt = DateTime.UtcNow + expiry;

        // Update memory cache expiration
        _expirations[key] = expiresAt;

        // Update file expiration
        await _lock.WaitAsync(ct);
        try
        {
          var path = GetFilePath(key);
          if (File.Exists(path))
          {
            var json = await File.ReadAllTextAsync(path, ct);
            var entry = JsonSerializer.Deserialize<CacheEntry<object>>(json);
            if (entry != null)
            {
              entry.ExpiresAt = expiresAt;
              await File.WriteAllTextAsync(
                  path,
                  JsonSerializer.Serialize(entry),
                  ct);
              return true;
            }
          }
        }
        finally
        {
          _lock.Release();
        }
      }

      return false;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to set expiration for cache entry: {Key}", key);
      throw new StorageException(
          $"Failed to set expiration for cache entry: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task ClearAsync(CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

    await _lock.WaitAsync(ct);
    try
    {
      // Clear memory cache
      _cache.Clear();
      _expirations.Clear();

      // Clear disk cache
      foreach (var file in Directory.GetFiles(_storageDirectory))
      {
        File.Delete(file);
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Failed to clear cache");
      throw new StorageException(
          "Failed to clear cache",
          ex,
          StorageErrorCode.OperationFailed);
    }
    finally
    {
      _lock.Release();
    }
  }

  private void CleanupCallback(object state)
  {
    try
    {
      var now = DateTime.UtcNow;
      var expired = _expirations
          .Where(kvp => kvp.Value <= now)
          .Select(kvp => kvp.Key)
          .ToList();

      foreach (var key in expired)
      {
        _cache.TryRemove(key, out _);
        _expirations.TryRemove(key, out _);

        var path = GetFilePath(key);
        if (File.Exists(path))
        {
          File.Delete(path);
        }
      }

      _logger.LogTrace("Cleaned up {Count} expired cache entries", expired.Count);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error during cache cleanup");
    }
  }

  private string GetFilePath(string key)
  {
    var filename = Convert.ToBase64String(Encoding.UTF8.GetBytes(key))
        .Replace('/', '_')
        .Replace('+', '-');
    return Path.Combine(_storageDirectory, filename);
  }

  private static void ValidateKey(string key)
  {
    if (string.IsNullOrEmpty(key))
    {
      throw new ArgumentException("Key cannot be empty", nameof(key));
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

      // Stop cleanup timer
      await _cleanupTimer.DisposeAsync();

      // Clear caches
      _cache.Clear();
      _expirations.Clear();
    }
    finally
    {
      _lock.Release();
      _lock.Dispose();
    }
  }
}
