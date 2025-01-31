using Ghost.Infrastructure.Storage;
namespace Ghost.Infrastructure.Data;

public interface IStorageRouter : IAsyncDisposable
{
    Task<T> ReadAsync<T>(string key, StorageType preferredStorage = StorageType.Cache);
    Task WriteAsync<T>(string key, T value, StorageType storage = StorageType.Both);
    Task DeleteAsync(string key, StorageType storage = StorageType.Both);
    Task<bool> ExistsAsync(string key, StorageType storage = StorageType.Cache);
}

public enum StorageType
{
    Cache,
    Database,
    Both
}

public class StorageRouter : IStorageRouter
{
    private readonly IRedisClient _cache;
    private readonly IPostgresClient _db;
    private readonly IPermissionsManager _permissions;

    public StorageRouter(
        IRedisClient cache,
        IPostgresClient db,
        IPermissionsManager permissions)
    {
        _cache = cache;
        _db = db;
        _permissions = permissions;
    }

    public async Task<T> ReadAsync<T>(string key, StorageType preferredStorage = StorageType.Cache)
    {
        await _permissions.EnsureCanRead(key);

        if (preferredStorage == StorageType.Cache)
        {
            var cached = await _cache.GetAsync<T>(key);
            if (cached != null)
                return cached;
        }

        // If cache miss or database preferred, read from database
        var sql = "SELECT data FROM storage WHERE key = @key";
        var result = await _db.QuerySingleAsync<T>(sql, new { key });

        // Update cache if we got from database
        if (preferredStorage != StorageType.Database)
        {
            await _cache.SetAsync(key, result);
        }

        return result;
    }

    public async Task WriteAsync<T>(string key, T value, StorageType storage = StorageType.Both)
    {
        await _permissions.EnsureCanWrite(key);

        if (storage == StorageType.Both || storage == StorageType.Database)
        {
            var sql = @"
                INSERT INTO storage (key, data) 
                VALUES (@key, @value)
                ON CONFLICT (key) DO UPDATE 
                SET data = @value";
            await _db.ExecuteAsync(sql, new { key, value });
        }

        if (storage == StorageType.Both || storage == StorageType.Cache)
        {
            await _cache.SetAsync(key, value);
        }
    }

    public async Task DeleteAsync(string key, StorageType storage = StorageType.Both)
    {
        await _permissions.EnsureCanDelete(key);

        if (storage == StorageType.Both || storage == StorageType.Database)
        {
            var sql = "DELETE FROM storage WHERE key = @key";
            await _db.ExecuteAsync(sql, new { key });
        }

        if (storage == StorageType.Both || storage == StorageType.Cache)
        {
            await _cache.DeleteAsync(key);
        }
    }

    public async Task<bool> ExistsAsync(string key, StorageType storage = StorageType.Cache)
    {
        await _permissions.EnsureCanRead(key);

        if (storage == StorageType.Cache)
        {
            return await _cache.ExistsAsync(key);
        }

        var sql = "SELECT COUNT(*) FROM storage WHERE key = @key";
        var count = await _db.QuerySingleAsync<int>(sql, new { key });
        return count > 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
        await _db.DisposeAsync();
    }
}
