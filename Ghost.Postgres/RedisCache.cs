using StackExchange.Redis;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Ghost.Core.Data;

public class RedisCache : ICache
{
  private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
  private readonly ConnectionMultiplexer _redis;
  private readonly StackExchange.Redis.IDatabase _redisDb;
  private bool _disposed;

  public RedisCache(string connectionString)
  {
    _redis = ConnectionMultiplexer.Connect(connectionString);
    _redisDb = _redis.GetDatabase();
  }

  public async Task<bool> IsAvailableAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    TimeSpan latency = await _redisDb.PingAsync();
    return latency < TimeSpan.FromSeconds(1);
  }

  public async Task<long> GetStorageSizeAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));

    RedisResult info = await _redisDb.ExecuteAsync("INFO", "memory");
    string? usedMemory = info.ToString()
        .Split('\n')
        .FirstOrDefault(l => l.StartsWith("used_memory:"))
        ?.Split(':')[1];

    return long.TryParse(usedMemory, out long size) ? size : 0;
  }

  public async Task<T> GetAsync<T>(string key, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));

    RedisValue redisValue = await _redisDb.StringGetAsync(key);
    if (!redisValue.HasValue) return default(T);

    string value = redisValue.ToString();
    return string.IsNullOrEmpty(value) ? default(T?) : JsonSerializer.Deserialize<T>(value);
  }

  public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));

    string serialized = JsonSerializer.Serialize(value);
    return await _redisDb.StringSetAsync(key, serialized, expiry);
  }

  public async Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    return await _redisDb.KeyDeleteAsync(key);
  }

  public async Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    return await _redisDb.KeyExistsAsync(key);
  }

  public async Task<bool> ExpireAsync(string key, TimeSpan expiry, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));
    return await _redisDb.KeyExpireAsync(key, expiry);
  }

  public async Task ClearAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));

    var endpoints = _redis.GetEndPoints();
    foreach (EndPoint endpoint in endpoints)
    {
      IServer server = _redis.GetServer(endpoint);
      await server.FlushDatabaseAsync();
    }
  }
  public Task<List<T>> GetAllAsync<T>(T channelsActive)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(RedisCache));

    var channels = channelsActive.GetType().GetProperties()
        .Where(p => p.PropertyType == typeof(bool))
        .Select(p => p.Name)
        .ToList();

    var tasks = channels.Select(channel => GetAsync<T>(channel));
    return Task.WhenAll(tasks).ContinueWith(t => t.Result.ToList());
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed) return;

    await _lock.WaitAsync();
    try
    {
      if (_disposed) return;
      _disposed = true;

      await _redis.CloseAsync();
      _redis.Dispose();
      _lock.Dispose();
    }
    finally
    {
      _lock.Release();
    }
  }
}