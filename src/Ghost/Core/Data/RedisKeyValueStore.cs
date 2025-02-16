using Microsoft.Extensions.DependencyInjection;
namespace Ghost.Core.Data;

using System.Text.Json;

public interface IKeyValueStore : IStorageProvider
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
}

/// <summary>
/// SQLite-based key-value store implementation
/// </summary>
public class SQLiteKeyValueStore : IKeyValueStore
{
    private readonly IDatabaseClient _db;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;
    private bool _initialized;

    public SQLiteKeyValueStore(IDatabaseClient db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            // Create key-value table if it doesn't exist
            await _db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS key_value_store (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    expires_at TIMESTAMP NULL
                );
                CREATE INDEX IF NOT EXISTS idx_kvs_expires 
                ON key_value_store(expires_at);
            ");

            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SQLiteKeyValueStore));
        ValidateKey(key);

        await EnsureInitializedAsync(ct);

        var result = await _db.QuerySingleAsync<string>(@"
            SELECT value 
            FROM key_value_store 
            WHERE key = @key 
            AND (expires_at IS NULL OR expires_at > @now)
        ", new { key, now = DateTime.UtcNow }, ct);

        return result != null ? JsonSerializer.Deserialize<T>(result) : default;
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SQLiteKeyValueStore));
        ValidateKey(key);

        await EnsureInitializedAsync(ct);

        var serialized = JsonSerializer.Serialize(value);
        var expiresAt = expiry.HasValue ? DateTime.UtcNow + expiry.Value : (DateTime?)null;

        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            await _db.ExecuteAsync(@"
                INSERT OR REPLACE INTO key_value_store (key, value, expires_at)
                VALUES (@key, @value, @expiresAt)
            ", new { key, value = serialized, expiresAt }, ct);

            await tx.CommitAsync();
            return true;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SQLiteKeyValueStore));
        ValidateKey(key);

        await EnsureInitializedAsync(ct);

        var result = await _db.ExecuteAsync(@"
            DELETE FROM key_value_store 
            WHERE key = @key
        ", new { key }, ct);

        return result > 0;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SQLiteKeyValueStore));
        ValidateKey(key);

        await EnsureInitializedAsync(ct);

        var result = await _db.QuerySingleAsync<int>(@"
            SELECT 1 
            FROM key_value_store 
            WHERE key = @key 
            AND (expires_at IS NULL OR expires_at > @now)
        ", new { key, now = DateTime.UtcNow }, ct);

        return result == 1;
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return _db.IsAvailableAsync(ct);
    }

    public Task<long> GetStorageSizeAsync(CancellationToken ct = default)
    {
        return _db.GetStorageSizeAsync(ct);
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

            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Redis-based key-value store implementation
/// </summary>
public class RedisKeyValueStore : IKeyValueStore
{
    private readonly ICache _cache;
    private readonly TimeSpan _defaultExpiry = TimeSpan.FromDays(30);
    private bool _disposed;

    public RedisKeyValueStore(ICache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RedisKeyValueStore));
        ValidateKey(key);

        return _cache.GetAsync<T>(key, ct);
    }

    public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RedisKeyValueStore));
        ValidateKey(key);

        return _cache.SetAsync(key, value, expiry ?? _defaultExpiry, ct);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RedisKeyValueStore));
        ValidateKey(key);

        return _cache.DeleteAsync(key, ct);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RedisKeyValueStore));
        ValidateKey(key);

        return _cache.ExistsAsync(key, ct);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return _cache.IsAvailableAsync(ct);
    }

    public Task<long> GetStorageSizeAsync(CancellationToken ct = default)
    {
        return _cache.GetStorageSizeAsync(ct);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key cannot be empty", nameof(key));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        return _cache is IAsyncDisposable disposable ?
            disposable.DisposeAsync() :
            ValueTask.CompletedTask;
    }
}

/// <summary>
/// Service collection extensions
/// </summary>
public static class KeyValueStoreExtensions
{
    public static IServiceCollection AddKeyValueStore(this IServiceCollection services, bool useRedis)
    {
        if (useRedis)
        {
            services.AddSingleton<IKeyValueStore, RedisKeyValueStore>();
        }
        else
        {
            services.AddSingleton<IKeyValueStore, SQLiteKeyValueStore>();
        }

        return services;
    }
}