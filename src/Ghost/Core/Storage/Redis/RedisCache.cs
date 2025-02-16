using Ghost.Core.Data;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ghost.Core.Storage.Cache;

/// <summary>
/// Redis-based implementation of the cache interface
/// </summary>
public class RedisCache : ICache, IMessageBus
{
  private readonly ConnectionMultiplexer _redis;
  private readonly IDatabase _db;
  private readonly ISubscriber _sub;
  private readonly SemaphoreSlim _lock;
  private readonly ConcurrentDictionary<string, List<ChannelMessageQueue>> _subscriptions;
  private bool _disposed;

  public RedisCache(string connectionString)
  {
    try
    {
      _redis = ConnectionMultiplexer.Connect(connectionString);
      _db = _redis.GetDatabase();
      _sub = _redis.GetSubscriber();
      _lock = new SemaphoreSlim(1, 1);
      _subscriptions = new ConcurrentDictionary<string, List<ChannelMessageQueue>>();
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"Failed to connect to Redis: {ex.Message}");
      throw new StorageException(
          "Failed to connect to Redis",
          ex,
          StorageErrorCode.ConnectionFailed);
    }
  }

  public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));

    try
    {
      return await _db.PingAsync() < TimeSpan.FromSeconds(1);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Redis connectivity check failed");
      return false;
    }
  }

  public async Task<long> GetStorageSizeAsync(CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));

    try
    {
      var info = await _db.ExecuteAsync("INFO", "memory");
      var usedMemory = info.ToString()
          .Split('\n')
          .FirstOrDefault(l => l.StartsWith("used_memory:"))
          ?.Split(':')[1];

      return long.TryParse(usedMemory, out var size) ? size : 0;
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to get Redis memory usage");
      throw new StorageException(
          "Failed to get Redis memory usage",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(key);

    try
    {
      var value = await _db.StringGetAsync(key);
      if (!value.HasValue)
      {
        return default;
      }

      return JsonSerializer.Deserialize<T>(value.ToString());
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to get value from Redis: {Key}", key);
      throw new StorageException(
          $"Failed to get value from Redis: {key}",
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
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(key);

    try
    {
      var serialized = JsonSerializer.Serialize(value);
      return await _db.StringSetAsync(key, serialized, expiry);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to set value in Redis: {Key}", key);
      throw new StorageException(
          $"Failed to set value in Redis: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(key);

    try
    {
      return await _db.KeyDeleteAsync(key);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to delete key from Redis: {Key}", key);
      throw new StorageException(
          $"Failed to delete key from Redis: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(key);

    try
    {
      return await _db.KeyExistsAsync(key);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to check key existence in Redis: {Key}", key);
      throw new StorageException(
          $"Failed to check key existence in Redis: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<long> IncrementAsync(string key, long value = 1, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(key);

    try
    {
      return await _db.StringIncrementAsync(key, value);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to increment value in Redis: {Key}", key);
      throw new StorageException(
          $"Failed to increment value in Redis: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<bool> ExpireAsync(string key, TimeSpan expiry, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(key);

    try
    {
      return await _db.KeyExpireAsync(key, expiry);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to set expiry in Redis: {Key}", key);
      throw new StorageException(
          $"Failed to set expiry in Redis: {key}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task ClearAsync(CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));

    try
    {
      var endpoints = _redis.GetEndPoints();
      foreach (var endpoint in endpoints)
      {
        var server = _redis.GetServer(endpoint);
        await server.FlushDatabaseAsync();
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to clear Redis database");
      throw new StorageException(
          "Failed to clear Redis database",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task<long> PublishAsync(string channel, string message, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(channel);

    try
    {
      return await _sub.PublishAsync(channel, message);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to publish message to Redis channel: {Channel}", channel);
      throw new StorageException(
          $"Failed to publish message to Redis channel: {channel}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async IAsyncEnumerable<string> SubscribeAsync(
      string channel,
      [EnumeratorCancellation] CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(channel);

    var queue = new ChannelMessageQueue();

    try
    {
      // Add to subscriptions
      _subscriptions.AddOrUpdate(
          channel,
          new List<ChannelMessageQueue> { queue },
          (_, list) => { list.Add(queue); return list; }
      );

      // Subscribe to channel
      //TODO: handle all the CS0618: Operator 'implicit StackExchange.Redis.RedisChannel.operator RedisChannel(string)' is obsolete: 'It is preferable to explicitly specify a PatternMode, or use the Literal/Pattern methods'
      await _sub.SubscribeAsync(channel, (_, message) =>
      {
        queue.Enqueue(message.ToString());
      });

      // Yield messages
      while (!ct.IsCancellationRequested)
      {
        if (queue.TryDequeue(out var message))
        {
          yield return message;
        }
        else
        {
          await Task.Delay(100, ct);
        }
      }
    }
    finally
    {
      // Cleanup subscription
      if (_subscriptions.TryGetValue(channel, out var subs))
      {
        subs.Remove(queue);
        if (!subs.Any())
        {
          _subscriptions.TryRemove(channel, out _);
          await _sub.UnsubscribeAsync(channel);
        }
      }
    }
  }

  public async Task<long> SubscriberCountAsync(string channel, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(channel);

    try
    {
      var count = await _sub.PublishAsync(channel, "");
      if (_subscriptions.TryGetValue(channel, out var localSubs))
      {
        count += localSubs.Count;
      }
      return count;
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to get subscriber count for Redis channel: {Channel}", channel);
      throw new StorageException(
          $"Failed to get subscriber count for Redis channel: {channel}",
          ex,
          StorageErrorCode.OperationFailed);
    }
  }

  public async Task UnsubscribeAsync(string channel, CancellationToken ct = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    ValidateKey(channel);

    try
    {
      await _sub.UnsubscribeAsync(channel);
      _subscriptions.TryRemove(channel, out _);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to unsubscribe from Redis channel: {Channel}", channel);
      throw new StorageException(
          $"Failed to unsubscribe from Redis channel: {channel}",
          ex,
          StorageErrorCode.OperationFailed);
    }
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

      // Unsubscribe all channels
      foreach (var channel in _subscriptions.Keys)
      {
        await _sub.UnsubscribeAsync(channel);
      }
      _subscriptions.Clear();

      // Dispose Redis connection
      if (_redis != null)
      {
        await _redis.CloseAsync();
        _redis.Dispose();
      }
    }
    finally
    {
      _lock.Release();
      _lock.Dispose();
    }
  }
}

/// <summary>
/// Thread-safe queue for channel messages
/// </summary>
internal class ChannelMessageQueue
{
  private readonly ConcurrentQueue<string> _queue = new();

  public void Enqueue(string message)
  {
    _queue.Enqueue(message);
  }

  public bool TryDequeue(out string message)
  {
    return _queue.TryDequeue(out message);
  }
}
