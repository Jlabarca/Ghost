using System.Net;
using System.Text.Json;
using Ghost.Logging;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
namespace Ghost.Data.Implementations;

/// <summary>
///     Redis implementation of the cache interface.
/// </summary>
public class RedisCache : ICache
{
    private readonly IDatabase _db;
    private readonly TimeSpan _defaultExpiry = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private readonly IGhostLogger _logger;
    private readonly ConnectionMultiplexer _redis;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="RedisCache" /> class.
    /// </summary>
    /// <param name="connectionString">The Redis connection string.</param>
    /// <param name="logger">The logger.</param>
    public RedisCache(string connectionString, IGhostLogger logger)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new ArgumentNullException(nameof(connectionString));
        }

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _redis = ConnectionMultiplexer.Connect(connectionString);
        _db = _redis.GetDatabase();
    }

    /// <summary>
    ///     Gets the name of this cache provider.
    /// </summary>
    public string Name => "Redis";

    /// <summary>
    ///     Indicates that this is a distributed cache.
    /// </summary>
    public bool IsDistributed => true;

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        RedisValue value = await _db.StringGetAsync(key);
        if (!value.HasValue)
        {
            return default(T?);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize Redis value for key {Key}", key);
            return default(T?);
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        if (value == null)
        {
            return await _db.KeyDeleteAsync(key);
        }

        try
        {
            string serializedValue = JsonSerializer.Serialize(value);
            return await _db.StringSetAsync(key, serializedValue, expiry ?? _defaultExpiry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to serialize value for key {Key}", key);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _db.KeyDeleteAsync(key);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _db.KeyExistsAsync(key);
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        string[] keyArray = keys as string[] ?? keys.ToArray();
        var result = new Dictionary<string, T?>();

        // Process in batches of 50
        const int batchSize = 50;
        for (int i = 0; i < keyArray.Length; i += batchSize)
        {
            string[] batch = keyArray.Skip(i).Take(batchSize).ToArray();
            var redisKeys = batch.Select(k => (RedisKey)k).ToArray();

            var values = await _db.StringGetAsync(redisKeys);

            for (int j = 0; j < batch.Length; j++)
            {
                RedisValue value = values[j];
                if (!value.HasValue)
                {
                    continue;
                }

                try
                {
                    T? deserializedValue = JsonSerializer.Deserialize<T>(value.ToString());
                    result[batch[j]] = deserializedValue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to deserialize Redis value for key {Key}", batch[j]);
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> SetManyAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        if (items.Count == 0)
        {
            return true;
        }

        // Process in batches of 50
        const int batchSize = 50;
        string[] allKeys = items.Keys.ToArray();
        bool success = true;

        for (int i = 0; i < allKeys.Length; i += batchSize)
        {
            string[] batch = allKeys.Skip(i).Take(batchSize).ToArray();
            var tasks = new List<Task<bool>>();

            foreach (string key in batch)
            {
                T value = items[key];
                tasks.Add(SetAsync(key, value, expiry, ct));
            }

            bool[] results = await Task.WhenAll(tasks);
            success = success && results.All(r => r);
        }

        return success;
    }

    /// <inheritdoc />
    public async Task<int> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        string[] keyArray = keys as string[] ?? keys.ToArray();
        if (keyArray.Length == 0)
        {
            return 0;
        }

        var redisKeys = keyArray.Select(k => (RedisKey)k).ToArray();
        long result = await _db.KeyDeleteAsync(redisKeys);

        return (int)result;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateExpiryAsync(string key, TimeSpan expiry, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _db.KeyExpireAsync(key, expiry);
    }

    /// <inheritdoc />
    public async Task<bool> ClearAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // Warning: This is a potentially dangerous operation in production
        // It flushes the entire Redis database, which may affect other applications
        _logger.LogWarning("Clearing entire Redis cache - this may affect other applications using the same Redis instance");

        try
        {
            var endpoints = _redis.GetEndPoints();
            foreach (EndPoint endpoint in endpoints)
            {
                IServer server = _redis.GetServer(endpoint);
                await server.FlushDatabaseAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear Redis cache");
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _redis.Dispose();
            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RedisCache));
        }
    }
}
