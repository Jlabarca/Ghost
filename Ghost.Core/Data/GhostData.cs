using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ghost.Core.Data;

public class GhostData : IGhostData
{
    private readonly IDatabaseClient _db;
    private readonly IKeyValueStore _kvStore;
    private readonly ICache _cache;
    private readonly ISchemaManager _schema;
    private readonly TimeSpan _defaultCacheExpiry = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;


    public DatabaseType DatabaseType => _db.DatabaseType;
    public ISchemaManager Schema => _schema;
    public IDatabaseClient GetDatabaseClient() => _db;

    public GhostData(
            IDatabaseClient db,
            IKeyValueStore kvStore,
            ICache cache,
            ISchemaManager schema)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _kvStore = kvStore ?? throw new ArgumentNullException(nameof(kvStore));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public Task InitializeAsync()
    {
        G.LogInfo("Initializing GhostData...");
        return Task.CompletedTask;
    }

    // Key-value operations delegated to IKeyValueStore
    public Task<T?> GetValueAsync<T>(string key, CancellationToken ct = default(CancellationToken))
    {
        ValidateState();
        return _kvStore.GetAsync<T>(key, ct);
    }

    public Task SetValueAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        ValidateState();
        return _kvStore.SetAsync(key, value, expiry, ct);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        ValidateState();
        return _kvStore.DeleteAsync(key, ct);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ValidateState();
        return _kvStore.ExistsAsync(key, ct);
    }

    // SQL operations with caching
    public async Task<T> QuerySingleAsync<T>(string sql, object param = null, CancellationToken ct = default)
    {
        ValidateState();

        var cacheKey = GetCacheKey(sql, param);
        var cached = await _cache.GetAsync<T>(cacheKey, ct);
        if (cached != null) return cached;

        var result = await _db.QuerySingleAsync<T>(sql, param, ct);
        if (result != null)
        {
            await _cache.SetAsync(cacheKey, result, _defaultCacheExpiry, ct);
        }
        return result;
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null, CancellationToken ct = default)
    {
        ValidateState();

        var cacheKey = GetCacheKey(sql, param);
        var cached = await _cache.GetAsync<IEnumerable<T>>(cacheKey, ct);
        if (cached != null) return cached;

        var results = await _db.QueryAsync<T>(sql, param, ct);
        if (results?.Any() == true)
        {
            await _cache.SetAsync(cacheKey, results, _defaultCacheExpiry, ct);
        }
        return results ?? Enumerable.Empty<T>();
    }

    public async Task<int> ExecuteAsync(string sql, object param = null, CancellationToken ct = default)
    {
        ValidateState();

        await InvalidateRelatedCachesAsync(sql);
        return await _db.ExecuteAsync(sql, param, ct);
    }

    public Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        ValidateState();
        return _db.BeginTransactionAsync(ct);
    }

    public Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
    {
        ValidateState();
        return _db.TableExistsAsync(tableName, ct);
    }

    public Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default)
    {
        ValidateState();
        return _db.GetTableNamesAsync(ct);
    }

    private void ValidateState()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));
    }

    private static string GetCacheKey(string sql, object param = null)
    {
        using var sha = SHA256.Create();
        var key = param == null ? sql : $"{sql}_{JsonSerializer.Serialize(param)}";
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        return $"query:{Convert.ToBase64String(hash)}";
    }

    private async Task InvalidateRelatedCachesAsync(string sql)
    {
        var tables = GetAffectedTables(sql);
        foreach (var table in tables)
        {
            await _cache.DeleteAsync($"query:*{table.ToLowerInvariant()}*");
        }
    }

    private static IEnumerable<string> GetAffectedTables(string sql)
    {
        var tables = new HashSet<string>();
        var words = sql.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for(var i = 0; i < words.Length - 1; i++)
        {
            var word = words[i].ToUpperInvariant();
            if (word is "FROM" or "JOIN" or "INTO" or "UPDATE")
            {
                var tableName = words[i + 1].Trim('`', '[', ']', '"', ';');
                tables.Add(tableName);
            }
        }

        return tables;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;

            if (_db is IAsyncDisposable dbDisposable)
                await dbDisposable.DisposeAsync();

            if (_kvStore is IAsyncDisposable kvDisposable)
                await kvDisposable.DisposeAsync();

            if (_cache is IAsyncDisposable cacheDisposable)
                await cacheDisposable.DisposeAsync();

            _lock.Release();
            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }
}