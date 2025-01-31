using StackExchange.Redis;
using System.Text.Json;

namespace Ghost.Infrastructure.Storage;

public interface IRedisClient : IAsyncDisposable
{
    Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null);
    Task<T?> GetAsync<T>(string key);
    Task<bool> DeleteAsync(string key);
    Task<bool> ExistsAsync(string key);
    Task<long> PublishAsync(string channel, string message);
    IAsyncEnumerable<string> SubscribeAsync(string channel, CancellationToken cancellationToken = default);
}

public class RedisClient : IRedisClient
{
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ISubscriber _sub;
    private readonly Dictionary<string, List<IAsyncDisposable>> _subscriptions;
    private readonly SemaphoreSlim _semaphore;

    public RedisClient(string connectionString)
    {
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _db = _redis.GetDatabase();
        _sub = _redis.GetSubscriber();
        _subscriptions = new Dictionary<string, List<IAsyncDisposable>>();
        _semaphore = new SemaphoreSlim(1, 1);
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null)
    {
        try
        {
            var serialized = JsonSerializer.Serialize(value);
            return await _db.StringSetAsync(key, serialized, expiry);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to set value in Redis", 
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        try
        {
            var value = await _db.StringGetAsync(key);
            if (!value.HasValue)
                return default;

            return JsonSerializer.Deserialize<T>(value!);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to get value from Redis", 
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<bool> DeleteAsync(string key)
    {
        try
        {
            return await _db.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to delete key from Redis", 
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<bool> ExistsAsync(string key)
    {
        try
        {
            return await _db.KeyExistsAsync(key);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to check key existence in Redis", 
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<long> PublishAsync(string channel, string message)
    {
        try
        {
            return await _sub.PublishAsync(channel, message);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to publish message", 
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async IAsyncEnumerable<string> SubscribeAsync(
        string channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        yield return string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            foreach (var subscriptions in _subscriptions.Values)
            {
                foreach (var subscription in subscriptions)
                {
                    await subscription.DisposeAsync();
                }
            }
            _subscriptions.Clear();
        }
        finally
        {
            _semaphore.Release();
        }

        if (_redis != null)
        {
            await _redis.DisposeAsync();
        }
        _semaphore.Dispose();
    }
}
