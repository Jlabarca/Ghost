using System.Collections.Concurrent;
using System.Text.Json;
namespace Ghost.Infrastructure.Storage;


public class LocalCacheClient : IRedisClient
{
  private readonly string _dataDir;
  private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
  private readonly Timer _cleanupTimer;

  public LocalCacheClient(string dataDir)
  {
    _dataDir = dataDir;
    _cleanupTimer = new Timer(CleanupCallback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    LoadPersistedCache();
  }

  private void LoadPersistedCache()
  {
    var cacheFile = Path.Combine(_dataDir, "cache.json");
    if (File.Exists(cacheFile))
    {
      var json = File.ReadAllText(cacheFile);
      var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
      foreach (var entry in entries)
      {
        if (!entry.Value.IsExpired)
        {
          _cache[entry.Key] = entry.Value;
        }
      }
    }
  }

  private void PersistCache()
  {
    var cacheFile = Path.Combine(_dataDir, "cache.json");
    var json = JsonSerializer.Serialize(_cache);
    File.WriteAllText(cacheFile, json);
  }

  private void CleanupCallback(object state)
  {
    var expired = _cache.Where(kvp => kvp.Value.IsExpired)
        .Select(kvp => kvp.Key)
        .ToList();

    foreach (var key in expired)
    {
      _cache.TryRemove(key, out _);
    }

    PersistCache();
  }

  // Implement IRedisClient interface methods...
  public ValueTask DisposeAsync()
  {
    throw new NotImplementedException();
  }
  public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null)
  {
    throw new NotImplementedException();
  }
  public Task<T?> GetAsync<T>(string key)
  {
    throw new NotImplementedException();
  }
  public Task<bool> DeleteAsync(string key)
  {
    throw new NotImplementedException();
  }
  public Task<bool> ExistsAsync(string key)
  {
    throw new NotImplementedException();
  }
  public Task<long> PublishAsync(string channel, string message)
  {
    throw new NotImplementedException();
  }
  public IAsyncEnumerable<string> SubscribeAsync(string channel, CancellationToken cancellationToken = default)
  {
    throw new NotImplementedException();
  }
}