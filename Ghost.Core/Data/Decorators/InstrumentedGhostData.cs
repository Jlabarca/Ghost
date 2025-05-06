using System.Diagnostics;
using Ghost.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace Ghost.Core.Data;

/// <summary>
/// Decorator that adds metrics, tracing, and logging to any IGhostData implementation.
/// Records performance metrics and traces for all data operations.
/// </summary>
public class InstrumentedGhostData : IGhostData
{
    private readonly IGhostData _inner;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<InstrumentedGhostData> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstrumentedGhostData"/> class.
    /// </summary>
    /// <param name="inner">The decorated IGhostData implementation.</param>
    /// <param name="metrics">The metrics collector.</param>
    /// <param name="logger">The logger.</param>
    public InstrumentedGhostData(
            IGhostData inner,
            IMetricsCollector metrics,
            ILogger<InstrumentedGhostData> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public DatabaseType DatabaseType => _inner.DatabaseType;

    /// <inheritdoc />
    public IDatabaseClient GetDatabaseClient() => _inner.GetDatabaseClient();

        #region Key-Value Operations

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.GetAsync");
        activity?.SetTag("key", key);
        activity?.SetTag("type", typeof(T).Name);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.GetAsync<T>(key, ct);

            sw.Stop();
            RecordSuccess("get", sw.ElapsedMilliseconds);
            activity?.SetTag("cache_hit", result != null);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("get", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.SetAsync");
        activity?.SetTag("key", key);
        activity?.SetTag("type", typeof(T).Name);
        activity?.SetTag("expiry", expiry?.ToString() ?? "default");

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.SetAsync(key, value, expiry, ct);

            sw.Stop();
            RecordSuccess("set", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("set", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.DeleteAsync");
        activity?.SetTag("key", key);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.DeleteAsync(key, ct);

            sw.Stop();
            RecordSuccess("delete", sw.ElapsedMilliseconds);
            activity?.SetTag("deleted", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("delete", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.ExistsAsync");
        activity?.SetTag("key", key);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.ExistsAsync(key, ct);

            sw.Stop();
            RecordSuccess("exists", sw.ElapsedMilliseconds);
            activity?.SetTag("exists", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("exists", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

        #endregion

        #region Batch Key-Value Operations

    /// <inheritdoc />
    public async Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var keysList = keys as List<string> ?? keys.ToList();

        using var activity = StartActivity("GhostData.GetBatchAsync");
        activity?.SetTag("keys_count", keysList.Count);
        activity?.SetTag("type", typeof(T).Name);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.GetBatchAsync<T>(keysList, ct);

            sw.Stop();
            RecordBatchSuccess("get_batch", keysList.Count, sw.ElapsedMilliseconds);
            activity?.SetTag("found_count", result.Count(kv => kv.Value != null));

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordBatchError("get_batch", keysList.Count, sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.SetBatchAsync");
        activity?.SetTag("items_count", items.Count);
        activity?.SetTag("type", typeof(T).Name);
        activity?.SetTag("expiry", expiry?.ToString() ?? "default");

        var sw = Stopwatch.StartNew();
        try
        {
            await _inner.SetBatchAsync(items, expiry, ct);

            sw.Stop();
            RecordBatchSuccess("set_batch", items.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordBatchError("set_batch", items.Count, sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var keysList = keys as List<string> ?? keys.ToList();

        using var activity = StartActivity("GhostData.DeleteBatchAsync");
        activity?.SetTag("keys_count", keysList.Count);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.DeleteBatchAsync(keysList, ct);

            sw.Stop();
            RecordBatchSuccess("delete_batch", keysList.Count, sw.ElapsedMilliseconds);
            activity?.SetTag("deleted_count", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordBatchError("delete_batch", keysList.Count, sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

        #endregion

        #region SQL Operations

    /// <inheritdoc />
    public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.QuerySingleAsync");
        activity?.SetTag("sql_hash", GetSqlHash(sql));
        activity?.SetTag("type", typeof(T).Name);
        activity?.SetTag("has_params", param != null);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.QuerySingleAsync<T>(sql, param, ct);

            sw.Stop();
            RecordSuccess("query_single", sw.ElapsedMilliseconds);
            activity?.SetTag("found", result != null);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("query_single", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.QueryAsync");
        activity?.SetTag("sql_hash", GetSqlHash(sql));
        activity?.SetTag("type", typeof(T).Name);
        activity?.SetTag("has_params", param != null);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.QueryAsync<T>(sql, param, ct);
            var resultList = result as ICollection<T> ?? result.ToList();

            sw.Stop();
            RecordSuccess("query", sw.ElapsedMilliseconds);
            activity?.SetTag("result_count", resultList.Count);

            return resultList;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("query", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.ExecuteAsync");
        activity?.SetTag("sql_hash", GetSqlHash(sql));
        activity?.SetTag("has_params", param != null);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.ExecuteAsync(sql, param, ct);

            sw.Stop();
            RecordSuccess("execute", sw.ElapsedMilliseconds);
            activity?.SetTag("rows_affected", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("execute", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var commandsList = commands as List<(string sql, object? param)> ?? commands.ToList();

        using var activity = StartActivity("GhostData.ExecuteBatchAsync");
        activity?.SetTag("commands_count", commandsList.Count);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.ExecuteBatchAsync(commandsList, ct);

            sw.Stop();
            RecordBatchSuccess("execute_batch", commandsList.Count, sw.ElapsedMilliseconds);
            activity?.SetTag("rows_affected", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordBatchError("execute_batch", commandsList.Count, sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

        #endregion

        #region Transaction Support

    /// <inheritdoc />
    public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.BeginTransactionAsync");

        var sw = Stopwatch.StartNew();
        try
        {
            var transaction = await _inner.BeginTransactionAsync(ct);

            sw.Stop();
            RecordSuccess("begin_transaction", sw.ElapsedMilliseconds);

            // Wrap the transaction with instrumentation
            return new InstrumentedGhostTransaction(transaction, _metrics, _logger);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("begin_transaction", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

        #endregion

        #region Schema Operations

    /// <inheritdoc />
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.TableExistsAsync");
        activity?.SetTag("table_name", tableName);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.TableExistsAsync(tableName, ct);

            sw.Stop();
            RecordSuccess("table_exists", sw.ElapsedMilliseconds);
            activity?.SetTag("exists", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("table_exists", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        using var activity = StartActivity("GhostData.GetTableNamesAsync");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.GetTableNamesAsync(ct);
            var resultList = result as ICollection<string> ?? result.ToList();

            sw.Stop();
            RecordSuccess("get_table_names", sw.ElapsedMilliseconds);
            activity?.SetTag("table_count", resultList.Count);

            return resultList;
        }
        catch (Exception ex)
        {
            sw.Stop();
            RecordError("get_table_names", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

        #endregion

        #region Helper Methods

    /// <summary>
    /// Starts a new activity for tracing.
    /// </summary>
    /// <param name="name">The name of the activity.</param>
    /// <returns>The started activity.</returns>
    private static Activity? StartActivity(string name)
    {
        var activity = new Activity(name);
        activity.Start();
        return activity;
    }

    /// <summary>
    /// Records success metrics for an operation.
    /// </summary>
    /// <param name="operation">The name of the operation.</param>
    /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
    private void RecordSuccess(string operation, long elapsedMs)
    {
        _metrics.RecordLatency($"ghost.data.{operation}", elapsedMs);
        _metrics.IncrementCounter($"ghost.data.{operation}.success");

        if (elapsedMs > 100)
        {
            _logger.LogDebug("Slow {Operation} operation: {ElapsedMs}ms",
                    operation, elapsedMs);
        }
    }

    /// <summary>
    /// Records error metrics for an operation.
    /// </summary>
    /// <param name="operation">The name of the operation.</param>
    /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
    /// <param name="ex">The exception that occurred.</param>
    private void RecordError(string operation, long elapsedMs, Exception ex)
    {
        _metrics.RecordLatency($"ghost.data.{operation}", elapsedMs);
        _metrics.IncrementCounter($"ghost.data.{operation}.error");

        _logger.LogError(ex, "{Operation} operation failed after {ElapsedMs}ms",
                operation, elapsedMs);
    }

    /// <summary>
    /// Records success metrics for a batch operation.
    /// </summary>
    /// <param name="operation">The name of the operation.</param>
    /// <param name="batchSize">The size of the batch.</param>
    /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
    private void RecordBatchSuccess(string operation, int batchSize, long elapsedMs)
    {
        _metrics.RecordLatency($"ghost.data.{operation}", elapsedMs);
        _metrics.IncrementCounter($"ghost.data.{operation}.success");
        _metrics.RecordGauge($"ghost.data.{operation}.batch_size", batchSize);
        _metrics.RecordLatency($"ghost.data.{operation}.per_item", elapsedMs / Math.Max(1, batchSize));

        if (elapsedMs > 500 || (batchSize > 0 && elapsedMs / batchSize > 20))
        {
            _logger.LogDebug("Slow {Operation} operation: {ElapsedMs}ms for {BatchSize} items ({PerItem}ms per item)",
                    operation, elapsedMs, batchSize, elapsedMs / Math.Max(1, batchSize));
        }
    }

    /// <summary>
    /// Records error metrics for a batch operation.
    /// </summary>
    /// <param name="operation">The name of the operation.</param>
    /// <param name="batchSize">The size of the batch.</param>
    /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
    /// <param name="ex">The exception that occurred.</param>
    private void RecordBatchError(string operation, int batchSize, long elapsedMs, Exception ex)
    {
        _metrics.RecordLatency($"ghost.data.{operation}", elapsedMs);
        _metrics.IncrementCounter($"ghost.data.{operation}.error");
        _metrics.RecordGauge($"ghost.data.{operation}.batch_size", batchSize);

        _logger.LogError(ex, "{Operation} operation failed after {ElapsedMs}ms for {BatchSize} items",
                operation, elapsedMs, batchSize);
    }

    /// <summary>
    /// Generates a simple hash for a SQL query for logging purposes.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>A hash code for the SQL query.</returns>
    private static string GetSqlHash(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return "empty";
        return Math.Abs(sql.GetHashCode()).ToString("X8");
    }

    /// <summary>
    /// Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InstrumentedGhostData));
    }

        #endregion

        #region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _inner.DisposeAsync();
    }

        #endregion

        #region Instrumented Transaction Implementation

    /// <summary>
    /// Wraps a transaction with instrumentation.
    /// </summary>
    private class InstrumentedGhostTransaction : IGhostTransaction
    {
        private readonly IGhostTransaction _inner;
        private readonly IMetricsCollector _metrics;
        private readonly ILogger _logger;
        private readonly Stopwatch _transactionTimer;
        private bool _disposed;

        public InstrumentedGhostTransaction(
                IGhostTransaction inner,
                IMetricsCollector metrics,
                ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _transactionTimer = Stopwatch.StartNew();
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            using var activity = StartActivity("GhostTransaction.CommitAsync");

            var sw = Stopwatch.StartNew();
            try
            {
                await _inner.CommitAsync(ct);

                sw.Stop();
                RecordSuccess("transaction_commit", sw.ElapsedMilliseconds);
                RecordSuccess("transaction_duration", _transactionTimer.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordError("transaction_commit", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            using var activity = StartActivity("GhostTransaction.RollbackAsync");

            var sw = Stopwatch.StartNew();
            try
            {
                await _inner.RollbackAsync(ct);

                sw.Stop();
                RecordSuccess("transaction_rollback", sw.ElapsedMilliseconds);
                _metrics.IncrementCounter("ghost.data.transaction_rollback");
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordError("transaction_rollback", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            using var activity = StartActivity("GhostTransaction.QuerySingleAsync");
            activity?.SetTag("sql_hash", GetSqlHash(sql));
            activity?.SetTag("type", typeof(T).Name);

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _inner.QuerySingleAsync<T>(sql, param, ct);

                sw.Stop();
                RecordSuccess("transaction_query_single", sw.ElapsedMilliseconds);
                activity?.SetTag("found", result != null);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordError("transaction_query_single", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            using var activity = StartActivity("GhostTransaction.QueryAsync");
            activity?.SetTag("sql_hash", GetSqlHash(sql));
            activity?.SetTag("type", typeof(T).Name);

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _inner.QueryAsync<T>(sql, param, ct);
                var resultList = result as ICollection<T> ?? result.ToList();

                sw.Stop();
                RecordSuccess("transaction_query", sw.ElapsedMilliseconds);
                activity?.SetTag("result_count", resultList.Count);

                return resultList;
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordError("transaction_query", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            using var activity = StartActivity("GhostTransaction.ExecuteAsync");
            activity?.SetTag("sql_hash", GetSqlHash(sql));

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _inner.ExecuteAsync(sql, param, ct);

                sw.Stop();
                RecordSuccess("transaction_execute", sw.ElapsedMilliseconds);
                activity?.SetTag("rows_affected", result);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordError("transaction_execute", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var commandsList = commands as List<(string sql, object? param)> ?? commands.ToList();

            using var activity = StartActivity("GhostTransaction.ExecuteBatchAsync");
            activity?.SetTag("commands_count", commandsList.Count);

            var sw = Stopwatch.StartNew();
            try
            {
                var result = await _inner.ExecuteBatchAsync(commandsList, ct);

                sw.Stop();
                RecordBatchSuccess("transaction_execute_batch", commandsList.Count, sw.ElapsedMilliseconds);
                activity?.SetTag("rows_affected", result);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                RecordBatchError("transaction_execute_batch", commandsList.Count, sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            // If the transaction lasted too long, log it as a potential issue
            if (_transactionTimer.ElapsedMilliseconds > 1000)
            {
                _logger.LogWarning("Long-running transaction: {ElapsedMs}ms",
                        _transactionTimer.ElapsedMilliseconds);
            }

            _transactionTimer.Stop();
            await _inner.DisposeAsync();
        }

        /// <summary>
        /// Starts a new activity for tracing.
        /// </summary>
        /// <param name="name">The name of the activity.</param>
        /// <returns>The started activity.</returns>
        private static Activity? StartActivity(string name)
        {
            var activity = new Activity(name);
            activity.Start();
            return activity;
        }

        /// <summary>
        /// Records success metrics for an operation.
        /// </summary>
        /// <param name="operation">The name of the operation.</param>
        /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
        private void RecordSuccess(string operation, long elapsedMs)
        {
            _metrics.RecordLatency($"ghost.data.{operation}", elapsedMs);
            _metrics.IncrementCounter($"ghost.data.{operation}.success");

            if (elapsedMs > 100)
            {
                _logger.LogDebug("Slow {Operation} operation: {ElapsedMs}ms",
                        operation, elapsedMs);
            }
        }

        /// <summary>
        /// Records error metrics for an operation.
        /// </summary>
        /// <param name="operation">The name of the operation.</param>
        /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
        /// <param name="ex">The exception that occurred.</param>
        private void RecordError(string operation, long elapsedMs, Exception ex)
        {
            _metrics.RecordLatency($"ghost.data.{operation}", elapsedMs);
            _metrics.IncrementCounter($"ghost.data.{operation}.error");

            _logger.LogError(ex, "{Operation} operation failed after {ElapsedMs}ms",
                    operation, elapsedMs);
        }

        /// <summary>
        /// Records success metrics for a batch operation.
        /// </summary>
        /// <param name="operation">The name of the operation.</param>
        /// <param name="batchSize">The size of the batch.</param>
        /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
        private void RecordBatchSuccess(string operation, int batchSize, long elapsedMs)
        {
            _metrics.RecordLatency($"ghost.data.{operation}", elapsedMs);
            _metrics.IncrementCounter($"ghost.data.{operation}.success");
            _metrics.RecordGauge($"ghost.data.{operation}.batch_size", batchSize);
            _metrics.RecordLatency($"ghost.data.{operation}.per_item", elapsedMs / Math.Max(1, batchSize));

            if (elapsedMs > 500 || (batchSize > 0 && elapsedMs / batchSize > 20))
            {
                _logger.LogDebug("Slow {Operation} operation: {ElapsedMs}ms for {BatchSize} items ({PerItem}ms per item)",
                        operation, elapsedMs, batchSize, elapsedMs / Math.Max(1, batchSize));
            }
        }

        /// <summary>
        /// Records error metrics for a batch operation.
        /// </summary>
        /// <param name="operation">The name of the operation.</param>
        /// <param name="batchSize">The size of the batch.</param>
        /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
        /// <param name="ex">The exception that occurred.</param>
        private void RecordBatchError(string operation, int batchSize, long elapsedMs, Exception ex)
        {
            _metrics.RecordLatency($"ghost.data.{operation}", elapsedMs);
            _metrics.IncrementCounter($"ghost.data.{operation}.error");
            _metrics.RecordGauge($"ghost.data.{operation}.batch_size", batchSize);

            _logger.LogError(ex, "{Operation} operation failed after {ElapsedMs}ms for {BatchSize} items",
                    operation, elapsedMs, batchSize);
        }

        /// <summary>
        /// Generates a simple hash for a SQL query for logging purposes.
        /// </summary>
        /// <param name="sql">The SQL query.</param>
        /// <returns>A hash code for the SQL query.</returns>
        private static string GetSqlHash(string sql)
        {
            if (string.IsNullOrEmpty(sql)) return "empty";
            return Math.Abs(sql.GetHashCode()).ToString("X8");
        }

        /// <summary>
        /// Throws if this object has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(InstrumentedGhostTransaction));
        }
    }

        #endregion
}