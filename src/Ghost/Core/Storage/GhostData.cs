using System.Text.Json;
using Ghost.Core.Storage.Cache;
using Ghost.Core.Storage.Database;

namespace Ghost.Core.Storage;

/// <summary>
/// Main interface for database access and schema management
/// </summary>
public interface IGhostData : IAsyncDisposable
{
    // Core querying
    Task<T> QuerySingleAsync<T>(string sql, object param = null);
    Task<T> QuerySingleOrDefaultAsync<T>(string sql, object param = null);
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null);
    Task<int> ExecuteAsync(string sql, object param = null);
    Task<IGhostTransaction> BeginTransactionAsync();

    // Schema management
    ISchemaManager Schema { get; }
    DatabaseType DatabaseType { get; }

    // Table info
    Task<bool> TableExistsAsync(string tableName);
    Task<IEnumerable<string>> GetTableNamesAsync();

    // Get underlying database client
    IDatabaseClient GetDatabaseClient();
}

/// <summary>
/// Main implementation of database access layer with caching
/// </summary>
public class GhostData : IGhostData
{
    private readonly IDatabaseClient _db;
    private readonly ICache _cache;
    private readonly ISchemaManager _schema;
    private readonly TimeSpan _defaultCacheExpiry = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public DatabaseType DatabaseType { get; }
    public ISchemaManager Schema => _schema;

    public GhostData(IDatabaseClient db, ICache cache)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        DatabaseType = db switch
        {
            PostgresClient => DatabaseType.PostgreSQL,
            SQLiteClient => DatabaseType.SQLite,
            _ => throw new NotSupportedException($"Database type {db.GetType().Name} not supported")
        };

        _schema = DatabaseType switch
        {
            DatabaseType.PostgreSQL => new PostgresSchemaManager(db),
            DatabaseType.SQLite => new SqliteSchemaManager(db),
            _ => throw new NotSupportedException($"Schema manager not available for {DatabaseType}")
        };
    }

    public async Task<T> QuerySingleAsync<T>(string sql, object param = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));
        try
        {
            // Try cache first
            var cacheKey = GetCacheKey(sql, param);
            var cached = await _cache.GetAsync<T>(cacheKey);
            if (cached != null)
            {
                G.LogDebug("Cache hit for query: {0}", sql);
                return cached;
            }

            // Execute query
            var result = await _db.QuerySingleAsync<T>(sql, param);

            // Cache result if not null
            if (result != null)
            {
                await _cache.SetAsync(cacheKey, result, _defaultCacheExpiry);
            }

            return result;
        }
        catch (Exception ex)
        {
            G.LogError("Failed to execute query: {0}", ex, sql);
            throw new GhostException(
                $"Query failed: {sql}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<T> QuerySingleOrDefaultAsync<T>(string sql, object param = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));
        try
        {
            // Try cache first
            var cacheKey = GetCacheKey(sql, param);
            var cached = await _cache.GetAsync<T>(cacheKey);
            if (cached != null)
            {
                G.LogDebug("Cache hit for query: {0}", sql);
                return cached;
            }

            // Execute query
            var result = await _db.QuerySingleAsync<T>(sql, param);

            // Cache result if not null
            if (result != null)
            {
                await _cache.SetAsync(cacheKey, result, _defaultCacheExpiry);
            }

            return result;
        }
        catch (Exception ex)
        {
            G.LogError("Failed to execute query: {0}", ex, sql);
            throw new GhostException(
                $"Query failed: {sql}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));
        try
        {
            // Try cache first
            var cacheKey = GetCacheKey(sql, param);
            var cached = await _cache.GetAsync<IEnumerable<T>>(cacheKey);
            if (cached != null)
            {
                G.LogDebug("Cache hit for query: {0}", sql);
                return cached;
            }

            // Execute query
            var results = await _db.QueryAsync<T>(sql, param);

            // Cache results if not empty
            if (results?.Any() == true)
            {
                await _cache.SetAsync(cacheKey, results, _defaultCacheExpiry);
            }

            return results ?? Enumerable.Empty<T>();
        }
        catch (Exception ex)
        {
            G.LogError("Failed to execute query: {0}", ex, sql);
            throw new GhostException(
                $"Query failed: {sql}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<int> ExecuteAsync(string sql, object param = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));
        try
        {
            // Execute command
            var result = await _db.ExecuteAsync(sql, param);

            // Invalidate relevant caches
            await InvalidateRelatedCachesAsync(sql);

            return result;
        }
        catch (Exception ex)
        {
            G.LogError("Failed to execute command: {0}", ex, sql);
            throw new GhostException(
                $"Command failed: {sql}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<IGhostTransaction> BeginTransactionAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));
        try
        {
            return await _db.BeginTransactionAsync();
        }
        catch (Exception ex)
        {
            G.LogError("Failed to begin transaction", ex);
            throw new GhostException(
                "Failed to begin transaction",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<bool> TableExistsAsync(string tableName)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));
        if (string.IsNullOrEmpty(tableName))
            throw new ArgumentException("Table name cannot be empty", nameof(tableName));

        try
        {
            var sql = DatabaseType switch
            {
                DatabaseType.SQLite =>
                    "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name",
                DatabaseType.PostgreSQL =>
                    "SELECT 1 FROM information_schema.tables WHERE table_name=@name",
                _ => throw new NotSupportedException("Unsupported database type")
            };

            var result = await _db.QuerySingleAsync<int?>(sql, new { name = tableName });
            return result.HasValue;
        }
        catch (Exception ex)
        {
            G.LogError("Failed to check table existence: {0}", ex, tableName);
            throw new GhostException(
                $"Failed to check table existence: {tableName}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<IEnumerable<string>> GetTableNamesAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));
        try
        {
            var sql = DatabaseType switch
            {
                DatabaseType.SQLite =>
                    "SELECT name FROM sqlite_master WHERE type='table'",
                DatabaseType.PostgreSQL =>
                    "SELECT table_name FROM information_schema.tables WHERE table_schema='public'",
                _ => throw new NotSupportedException("Unsupported database type")
            };

            return await _db.QueryAsync<string>(sql);
        }
        catch (Exception ex)
        {
            G.LogError("Failed to get table names", ex);
            throw new GhostException(
                "Failed to get table names",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }
    public IDatabaseClient GetDatabaseClient()
    {
        return _db;
    }

    private string GetCacheKey(string sql, object param)
    {
        var key = sql;
        if (param != null)
        {
            var jsonParams = JsonSerializer.Serialize(param);
            key = $"{sql}_{jsonParams}";
        }
        return $"query:{HashString(key)}";
    }

    private static string HashString(string input)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    private async Task InvalidateRelatedCachesAsync(string sql)
    {
        // Extract table names from SQL
        var tables = GetAffectedTables(sql);

        // Invalidate cache for each affected table
        foreach (var table in tables)
        {
            var pattern = $"query:*{table.ToLowerInvariant()}*";
            await _cache.DeleteAsync(pattern);
            G.LogDebug("Invalidated cache pattern: {0}", pattern);
        }
    }

    private static IEnumerable<string> GetAffectedTables(string sql)
    {
        var tables = new HashSet<string>();
        var words = sql.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < words.Length - 1; i++)
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

            if (_cache is IAsyncDisposable cacheDisposable)
                await cacheDisposable.DisposeAsync();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}

public enum DatabaseType
{
    SQLite,
    PostgreSQL
}