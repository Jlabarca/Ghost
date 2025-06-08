using System.Collections.Concurrent;
using Ghost.Data;
using Microsoft.Extensions.Logging;
namespace Ghost.Testing.InMemory;

/// <summary>
///     In-memory implementation of IGhostData for testing purposes.
/// </summary>
public class InMemoryGhostData : IGhostData
{
    private readonly ConcurrentDictionary<string, DateTime> _expirations = new ConcurrentDictionary<string, DateTime>();
    private readonly ConcurrentDictionary<string, object> _keyValueStore = new ConcurrentDictionary<string, object>();
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private readonly ILogger<InMemoryGhostData> _logger;
    private readonly ConcurrentDictionary<string, object> _sqlStore = new ConcurrentDictionary<string, object>();
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InMemoryGhostData" /> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public InMemoryGhostData(ILogger<InMemoryGhostData> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public DatabaseType DatabaseType => DatabaseType.InMemory;

    /// <inheritdoc />
    public IDatabaseClient GetDatabaseClient()
    {
        return new InMemoryDatabaseClient(this, _logger);
    }

#region Transaction Support

    /// <inheritdoc />
    public Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        _logger.LogDebug("BeginTransactionAsync");

        // For testing, we'll create a simple transaction that doesn't actually do anything
        InMemoryTransaction transaction = new InMemoryTransaction(this, _logger);
        return Task.FromResult<IGhostTransaction>(transaction);
    }

#endregion

#region IAsyncDisposable

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

            _keyValueStore.Clear();
            _expirations.Clear();
            _sqlStore.Clear();

            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

#endregion

#region Key-Value Operations

    /// <inheritdoc />
    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // Check if key exists and is not expired
        if (_keyValueStore.TryGetValue(key, out object? value) && !IsExpired(key))
        {
            if (value is T typedValue)
            {
                return Task.FromResult<T?>(typedValue);
            }

            // Try to convert the value
            try
            {
                T convertedValue = (T)Convert.ChangeType(value, typeof(T));
                return Task.FromResult<T?>(convertedValue);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to convert value of type {ActualType} to {ExpectedType} for key {Key}",
                        value.GetType().Name, typeof(T).Name, key);
            }
        }

        return Task.FromResult<T?>(default(T?));
    }

    /// <inheritdoc />
    public Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        _keyValueStore[key] = value;

        if (expiry.HasValue && expiry.Value > TimeSpan.Zero)
        {
            _expirations[key] = DateTime.UtcNow.Add(expiry.Value);
        }
        else
        {
            _expirations.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        bool removed = _keyValueStore.TryRemove(key, out _);
        _expirations.TryRemove(key, out _);

        return Task.FromResult(removed);
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return Task.FromResult(_keyValueStore.ContainsKey(key) && !IsExpired(key));
    }

#endregion

#region Batch Key-Value Operations

    /// <inheritdoc />
    public Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        var result = new Dictionary<string, T?>();

        foreach (string key in keys)
        {
            if (_keyValueStore.TryGetValue(key, out object? value) && !IsExpired(key))
            {
                if (value is T typedValue)
                {
                    result[key] = typedValue;
                }
                else
                {
                    // Try to convert the value
                    try
                    {
                        T convertedValue = (T)Convert.ChangeType(value, typeof(T));
                        result[key] = convertedValue;
                    }
                    catch
                    {
                        result[key] = default(T?);
                    }
                }
            }
            else
            {
                result[key] = default(T?);
            }
        }

        return Task.FromResult<IDictionary<string, T?>>(result);
    }

    /// <inheritdoc />
    public Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        foreach ((string key, T value) in items)
        {
            _keyValueStore[key] = value;

            if (expiry.HasValue && expiry.Value > TimeSpan.Zero)
            {
                _expirations[key] = DateTime.UtcNow.Add(expiry.Value);
            }
            else
            {
                _expirations.TryRemove(key, out _);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        int count = 0;
        foreach (string key in keys)
        {
            if (_keyValueStore.TryRemove(key, out _))
            {
                count++;
            }

            _expirations.TryRemove(key, out _);
        }

        return Task.FromResult(count);
    }

#endregion

#region SQL Operations

    /// <inheritdoc />
    public Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        _logger.LogDebug("QuerySingleAsync: {Sql}", sql);

        // For testing, we'll use a simple approach - return default or items stored with matching sql
        string key = GetSqlCacheKey(sql, param);

        if (_sqlStore.TryGetValue(key, out object? value))
        {
            if (value is T typedValue)
            {
                return Task.FromResult<T?>(typedValue);
            }
            if (value is IEnumerable<T> enumerable)
            {
                return Task.FromResult<T?>(enumerable.FirstOrDefault());
            }
        }

        return Task.FromResult<T?>(default(T?));
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        _logger.LogDebug("QueryAsync: {Sql}", sql);

        // For testing, we'll use a simple approach - return empty or items stored with matching sql
        string key = GetSqlCacheKey(sql, param);

        if (_sqlStore.TryGetValue(key, out object? value))
        {
            if (value is IEnumerable<T> enumerable)
            {
                return Task.FromResult(enumerable);
            }
            if (value is T typedValue)
            {
                return Task.FromResult<IEnumerable<T>>(new[]
                {
                        typedValue
                });
            }
        }

        return Task.FromResult<IEnumerable<T>>(Enumerable.Empty<T>());
    }

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        _logger.LogDebug("ExecuteAsync: {Sql}", sql);

        // For testing, we'll use a simple approach - store the SQL and return 1
        return Task.FromResult(1);
    }

    /// <inheritdoc />
    public Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        _logger.LogDebug("ExecuteBatchAsync with {Count} commands", commands.Count());

        // For testing, we'll just return the count of commands
        return Task.FromResult(commands.Count());
    }

#endregion

#region Schema Operations

    /// <inheritdoc />
    public Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // For testing, we'll just return true
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // For testing, we'll return some fake table names
        return Task.FromResult<IEnumerable<string>>(new[]
        {
                "users", "items",
                "logs"
        });
    }

#endregion

#region Testing Helpers

    /// <summary>
    ///     Sets a mock result for a SQL query.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="sql">The SQL query.</param>
    /// <param name="param">The parameters.</param>
    /// <param name="result">The result to return.</param>
    public void SetSqlResult<T>(string sql, object? param, T result)
    {
        string? key = GetSqlCacheKey(sql, param);
        _sqlStore[key] = result;
    }

    /// <summary>
    ///     Sets a mock result for a SQL query that returns multiple rows.
    /// </summary>
    /// <typeparam name="T">The type of the results.</typeparam>
    /// <param name="sql">The SQL query.</param>
    /// <param name="param">The parameters.</param>
    /// <param name="results">The results to return.</param>
    public void SetSqlResults<T>(string sql, object? param, IEnumerable<T> results)
    {
        string? key = GetSqlCacheKey(sql, param);
        _sqlStore[key] = results;
    }

    /// <summary>
    ///     Clears all stored data.
    /// </summary>
    public void Clear()
    {
        _keyValueStore.Clear();
        _expirations.Clear();
        _sqlStore.Clear();
    }

#endregion

#region Helper Methods

    /// <summary>
    ///     Checks if a key is expired.
    /// </summary>
    /// <param name="key">The key to check.</param>
    /// <returns>True if the key is expired; otherwise, false.</returns>
    private bool IsExpired(string key)
    {
        if (_expirations.TryGetValue(key, out DateTime expiry) && expiry < DateTime.UtcNow)
        {
            // Remove expired item
            _keyValueStore.TryRemove(key, out _);
            _expirations.TryRemove(key, out _);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Gets a cache key for a SQL query.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <param name="param">The parameters.</param>
    /// <returns>A unique cache key.</returns>
    private static string GetSqlCacheKey(string sql, object? param)
    {
        return $"SQL:{sql}:{param?.GetHashCode() ?? 0}";
    }

    /// <summary>
    ///     Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(InMemoryGhostData));
        }
    }

#endregion
}
/// <summary>
///     In-memory implementation of IGhostTransaction for testing purposes.
/// </summary>
internal class InMemoryTransaction : IGhostTransaction
{
    private readonly InMemoryGhostData _data;
    private readonly ILogger _logger;
    private bool _committed;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InMemoryTransaction" /> class.
    /// </summary>
    /// <param name="data">The in-memory data store.</param>
    /// <param name="logger">The logger.</param>
    public InMemoryTransaction(InMemoryGhostData data, ILogger logger)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task CommitAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        _logger.LogDebug("CommitAsync");
        _committed = true;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RollbackAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        _logger.LogDebug("RollbackAsync");
        _committed = false;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return _data.QuerySingleAsync<T>(sql, param, ct);
    }

    /// <inheritdoc />
    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return _data.QueryAsync<T>(sql, param, ct);
    }

    /// <inheritdoc />
    public Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return _data.ExecuteAsync(sql, param, ct);
    }

    /// <inheritdoc />
    public Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return _data.ExecuteBatchAsync(commands, ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_committed)
        {
            await RollbackAsync();
        }
    }

    /// <summary>
    ///     Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(InMemoryTransaction));
        }
    }
}
