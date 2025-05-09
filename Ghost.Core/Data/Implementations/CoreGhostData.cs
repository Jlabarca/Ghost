using System.Text.Json;
using Ghost.Core.Pooling;
using Microsoft.Extensions.Logging;

namespace Ghost.Core.Data.Implementations
{
    /// <summary>
    /// Core implementation of IGhostData with all required functionality.
    /// This class provides the base implementation for the decorator pattern.
    /// </summary>
    public class CoreGhostData : IGhostData
    {
        private readonly ConnectionPoolManager _connectionPool;
        private readonly IDatabaseClient _db;
        private readonly ICache _cache;
        private readonly ISchemaManager _schema;
        private readonly ILogger<CoreGhostData> _logger;
        private readonly TimeSpan _defaultCacheExpiry = TimeSpan.FromMinutes(5);
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="CoreGhostData"/> class.
        /// </summary>
        /// <param name="connectionPool">The connection pool manager.</param>
        /// <param name="db">The database client.</param>
        /// <param name="cache">The cache provider.</param>
        /// <param name="schema">The schema manager.</param>
        /// <param name="logger">The logger.</param>
        public CoreGhostData(
            ConnectionPoolManager connectionPool,
            IDatabaseClient db, 
            ICache cache,
            ISchemaManager schema,
            ILogger<CoreGhostData> logger)
        {
            _connectionPool = connectionPool ?? throw new ArgumentNullException(nameof(connectionPool));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _schema = schema ?? throw new ArgumentNullException(nameof(schema));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Gets the database client used by this data provider.
        /// </summary>
        /// <returns>The database client.</returns>
        public IDatabaseClient GetDatabaseClient() => _db;

        #region Key-Value Operations

        /// <inheritdoc />
        public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _cache.GetAsync<T>(key, ct);
        }

        /// <inheritdoc />
        public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            await _cache.SetAsync(key, value, expiry ?? _defaultCacheExpiry, ct);
        }

        /// <inheritdoc />
        public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _cache.DeleteAsync(key, ct);
        }

        /// <inheritdoc />
        public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _cache.ExistsAsync(key, ct);
        }

        #endregion

        #region Batch Key-Value Operations

        /// <inheritdoc />
        public async Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            
            var result = new Dictionary<string, T?>();
            var keysList = keys as List<string> ?? keys.ToList();
            
            // Process keys in batches
            const int batchSize = 50;
            for (var i = 0; i < keysList.Count; i += batchSize)
            {
                var batch = keysList.Skip(i).Take(batchSize);
                var batchResults = new Dictionary<string, T?>();
                
                // Get the values from Redis
                var db = await _connectionPool.GetRedisDatabaseAsync();
                var tasks = batch.Select(async key =>
                {
                    var value = await db.StringGetAsync(key);
                    if (value.HasValue)
                    {
                        try
                        {
                            var deserializedValue = JsonSerializer.Deserialize<T>(value.ToString());
                            return (key, deserializedValue);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to deserialize Redis value for key {Key}", key);
                        }
                    }
                    
                    return (key, default(T));
                });
                
                var results = await Task.WhenAll(tasks);
                foreach (var (key, value) in results)
                {
                    result[key] = value;
                }
            }
            
            return result;
        }

        /// <inheritdoc />
        public async Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            
            // Process items in batches
            const int batchSize = 50;
            var keysList = items.Keys.ToList();
            for (var i = 0; i < keysList.Count; i += batchSize)
            {
                var batch = keysList.Skip(i).Take(batchSize);
                
                // Set the values in Redis
                var db = await _connectionPool.GetRedisDatabaseAsync();
                var tasks = batch.Select(async key =>
                {
                    var value = items[key];
                    var serializedValue = JsonSerializer.Serialize(value);
                    
                    await db.StringSetAsync(
                        key,
                        serializedValue,
                        expiry ?? _defaultCacheExpiry);
                });
                
                await Task.WhenAll(tasks);
            }
        }

        /// <inheritdoc />
        public async Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            
            var count = 0;
            var keysList = keys as List<string> ?? keys.ToList();
            
            // Process keys in batches
            const int batchSize = 50;
            for (var i = 0; i < keysList.Count; i += batchSize)
            {
                var batch = keysList.Skip(i).Take(batchSize);
                
                // Delete the keys from Redis
                var db = await _connectionPool.GetRedisDatabaseAsync();
                var tasks = batch.Select(async key =>
                {
                    var deleted = await db.KeyDeleteAsync(key);
                    return deleted ? 1 : 0;
                });
                
                var results = await Task.WhenAll(tasks);
                count += results.Sum();
            }
            
            return count;
        }

        #endregion

        #region SQL Operations

        /// <inheritdoc />
        public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _db.QuerySingleAsync<T>(sql, param, ct);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _db.QueryAsync<T>(sql, param, ct);
        }

        /// <inheritdoc />
        public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _db.ExecuteAsync(sql, param, ct);
        }

        /// <inheritdoc />
        public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            
            var totalAffected = 0;
            
            await using var transaction = await BeginTransactionAsync(ct);
            try
            {
                foreach (var (sql, param) in commands)
                {
                    var affected = await transaction.ExecuteAsync(sql, param, ct);
                    totalAffected += affected;
                }
                
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
            
            return totalAffected;
        }

        #endregion

        #region Transaction Support

        /// <inheritdoc />
        public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _db.BeginTransactionAsync(ct);
        }

        #endregion

        #region Schema Operations

        /// <inheritdoc />
        public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _db.TableExistsAsync(tableName, ct);
        }

        /// <inheritdoc />
        public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return await _db.GetTableNamesAsync(ct);
        }

        #endregion

        #region IAsyncDisposable

        /// <inheritdoc />
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
                    
                _lock.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing CoreGhostData");
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Throws an ObjectDisposedException if this object has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed) 
                throw new ObjectDisposedException(nameof(CoreGhostData));
        }

        #endregion
    }
}
