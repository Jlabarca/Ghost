// Ghost/Core/Storage/GhostData.cs
using System.Data;
using Dapper;
using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Monitoring;
using Ghost.Infrastructure.Storage.Database;

namespace Ghost.Core.Storage;

/// <summary>
/// Main data access abstraction for Ghost applications.
/// Provides a unified interface for data operations while managing connection lifecycle.
/// </summary>
public interface IGhostData : IAsyncDisposable
{
    Task<T> QuerySingleAsync<T>(string sql, object param = null);
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null);
    Task<int> ExecuteAsync(string sql, object param = null);
    Task<IGhostTransaction> BeginTransactionAsync();
    Task<bool> TableExistsAsync(string tableName);
    Task<IEnumerable<string>> GetTableNamesAsync();
}

public class GhostData : IGhostData
{
    private readonly IDatabaseClient _db;
    private readonly IRedisClient _cache;
    private readonly ILogger _logger;
    private readonly TimeSpan _defaultCacheExpiry = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public GhostData(
        IDatabaseClient db,
        IRedisClient cache,
        ILogger<GhostData> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                _logger.LogDebug("Cache hit for query: {Sql}", sql);
                return cached;
            }

            // Execute query
            var result = await _db.QuerySingleAsync<T>(sql, param);

            // Cache result
            if (result != null)
            {
                await _cache.SetAsync(cacheKey, result, _defaultCacheExpiry);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute query: {Sql}", sql);
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
                _logger.LogDebug("Cache hit for query: {Sql}", sql);
                return cached;
            }

            // Execute query
            var results = await _db.QueryAsync<T>(sql, param);

            // Cache results
            if (results?.Any() == true)
            {
                await _cache.SetAsync(cacheKey, results, _defaultCacheExpiry);
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute query: {Sql}", sql);
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
            _logger.LogError(ex, "Failed to execute command: {Sql}", sql);
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
            _logger.LogError(ex, "Failed to begin transaction");
            throw new GhostException(
                "Failed to begin transaction",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<bool> TableExistsAsync(string tableName)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostData));

        try
        {
            var sql = _db switch
            {
                SQLiteClient => "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name",
                PostgresClient => "SELECT 1 FROM information_schema.tables WHERE table_name=@name",
                _ => throw new NotSupportedException("Unsupported database type")
            };

            var result = await _db.QuerySingleAsync<int?>(sql, new { name = tableName });
            return result.HasValue;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check table existence: {Table}", tableName);
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
            var sql = _db switch
            {
                SQLiteClient => "SELECT name FROM sqlite_master WHERE type='table'",
                PostgresClient => "SELECT table_name FROM information_schema.tables WHERE table_schema='public'",
                _ => throw new NotSupportedException("Unsupported database type")
            };

            return await _db.QueryAsync<string>(sql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get table names");
            throw new GhostException(
                "Failed to get table names",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    private string GetCacheKey(string sql, object param)
    {
        var key = sql;
        if (param != null)
        {
            var jsonParams = System.Text.Json.JsonSerializer.Serialize(param);
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
        // Simple cache invalidation strategy
        // In a real implementation, this would be more sophisticated
        // and based on actual table/query analysis
        var tables = GetAffectedTables(sql);
        foreach (var table in tables)
        {
            await _cache.DeleteAsync($"query:*{table.ToLowerInvariant()}*");
        }
    }

    private IEnumerable<string> GetAffectedTables(string sql)
    {
        // Simple table name extraction
        // In a real implementation, this would use proper SQL parsing
        var words = sql.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tables = new List<string>();

        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Equals("FROM", StringComparison.OrdinalIgnoreCase) ||
                words[i].Equals("JOIN", StringComparison.OrdinalIgnoreCase) ||
                words[i].Equals("INTO", StringComparison.OrdinalIgnoreCase) ||
                words[i].Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < words.Length)
                {
                    tables.Add(words[i + 1].Trim('`', '[', ']', '"'));
                }
            }
        }

        return tables;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _semaphore.WaitAsync();
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
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}

// Ghost/Core/Storage/Database/GhostDatabase.cs
namespace Ghost.Core.Storage.Database;

/// <summary>
/// Unified database interface that works with both SQLite and PostgreSQL.
/// Provides connection management and transaction support.
/// </summary>
public class GhostDatabase : IAsyncDisposable
{
    private readonly IDatabaseClient _db;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly List<string> _migrationScripts;
    private bool _disposed;

    public GhostDatabase(
        IDatabaseClient db,
        ILogger<GhostDatabase> logger,
        IEnumerable<string> migrationScripts = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _migrationScripts = migrationScripts?.ToList() ?? new List<string>();
    }

    public async Task InitializeAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostDatabase));

        await _semaphore.WaitAsync();
        try
        {
            // Create migrations table if it doesn't exist
            await _db.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS migrations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    applied_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )");

            // Apply pending migrations
            foreach (var script in _migrationScripts)
            {
                if (!await IsMigrationAppliedAsync(script))
                {
                    await ApplyMigrationAsync(script);
                }
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<bool> IsMigrationAppliedAsync(string migrationName)
    {
        var result = await _db.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM migrations WHERE name = @name",
            new { name = migrationName });
        return result > 0;
    }

    private async Task ApplyMigrationAsync(string script)
    {
        _logger.LogInformation("Applying migration: {Script}", script);

        await using var transaction = await _db.BeginTransactionAsync();
        try
        {
            // Execute migration script
            await _db.ExecuteAsync(script);

            // Record migration
            await _db.ExecuteAsync(
                "INSERT INTO migrations (name) VALUES (@name)",
                new { name = script });

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to apply migration: {Script}", script);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _semaphore.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;

            if (_db is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}

// Ghost/Core/Storage/Redis/GhostBus.cs
namespace Ghost.Core.Storage.Redis;

/// <summary>
/// Message bus implementation that supports both Redis and in-memory operation.
/// Provides pub/sub messaging and distributed coordination.
/// </summary>
public interface IGhostBus : IAsyncDisposable
{
    Task PublishAsync<T>(string channel, T message);
    IAsyncEnumerable<T> SubscribeAsync<T>(string channel, CancellationToken ct = default);
    Task<long> GetSubscriberCountAsync(string channel);
    Task<IEnumerable<string>> GetActiveChannelsAsync();
}

public class GhostBus : IGhostBus
{
    private readonly IRedisClient _client;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public GhostBus(IRedisClient client, ILogger<GhostBus> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task PublishAsync<T>(string channel, T message)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel))
            throw new ArgumentException("Channel cannot be empty", nameof(channel));

        try
        {
            var serialized = System.Text.Json.JsonSerializer.Serialize(message);
            await _client.PublishAsync(channel, serialized);

            _logger.LogDebug("Published message to channel: {Channel}", channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish message to channel: {Channel}", channel);
            throw new GhostException(
                $"Failed to publish message to channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async IAsyncEnumerable<T> SubscribeAsync<T>(
        string channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel))
            throw new ArgumentException("Channel cannot be empty", nameof(channel));

        try
        {
            _logger.LogDebug("Subscribing to channel: {Channel}", channel);

            await foreach (var message in _client.SubscribeAsync(channel, ct))
            {
                if (ct.IsCancellationRequested)
                {
                    yield break;
                }

                if (string.IsNullOrEmpty(message))
                {
                    continue;
                }

                try
                {
                    var deserialized = System.Text.Json.JsonSerializer.Deserialize<T>(message);
                    if (deserialized != null)
                    {
                        yield return deserialized;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize message from channel {Channel}", channel);
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Subscription cancelled for channel: {Channel}", channel);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in subscription to channel: {Channel}", channel);
            throw new GhostException(
                $"Subscription failed for channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<long> GetSubscriberCountAsync(string channel)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel))
            throw new ArgumentException("Channel cannot be empty", nameof(channel));

        try
        {
            // For Redis, get the actual subscriber count
            // For local cache, estimate based on active subscriptions
            return await _client.GetAsync<long>($"subscribers:{channel}") ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get subscriber count for channel: {Channel}", channel);
            throw new GhostException(
                $"Failed to get subscriber count for channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<IEnumerable<string>> GetActiveChannelsAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));

        try
        {
            var channels = await _client.GetAsync<HashSet<string>>("active_channels");
            return channels ?? Enumerable.Empty<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active channels");
            throw new GhostException(
                "Failed to get active channels",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _semaphore.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;

            if (_client is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }