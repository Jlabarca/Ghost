using System.Diagnostics;
using Ghost.Logging;
using Ghost.Monitoring;
using Microsoft.Extensions.Logging;
namespace Ghost.Data;

/// <summary>
///     Decorator that adds metrics, tracing, and logging to any IGhostData implementation.
///     Records performance metrics and traces for all data operations.
/// </summary>
public class InstrumentedGhostData : IGhostData, IAsyncDisposable, IDisposable
{
    private readonly IGhostData _inner;
    private readonly IGhostLogger _logger;
    private readonly IMetricsCollector _metrics;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InstrumentedGhostData" /> class.
    /// </summary>
    /// <param name="inner">The decorated IGhostData implementation.</param>
    /// <param name="metrics">The metrics collector.</param>
    /// <param name="logger">The logger.</param>
    public InstrumentedGhostData(
            IGhostData inner,
            IMetricsCollector metrics,
            IGhostLogger logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            // If inner implements IDisposable, dispose it synchronously
            if (_inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during synchronous disposal");
        }
    }

    /// <inheritdoc />
    public IDatabaseClient GetDatabaseClient()
    {
        return _inner.GetDatabaseClient();
    }

#region Transaction Support

    /// <inheritdoc />
    public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.BeginTransactionAsync");

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            IGhostTransaction transaction = await _inner.BeginTransactionAsync(ct);

            sw.Stop();
            await TrackSuccessMetricAsync("begin_transaction", sw.ElapsedMilliseconds);

            // Wrap the transaction with instrumentation
            return new InstrumentedGhostTransaction(transaction, _metrics, _logger);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("begin_transaction", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
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

        _disposed = true;
        await _inner.DisposeAsync();
    }

#endregion

#region Instrumented Transaction Implementation

    /// <summary>
    ///     Wraps a transaction with instrumentation.
    /// </summary>
    private class InstrumentedGhostTransaction : IGhostTransaction, IAsyncDisposable, IDisposable
    {
        private readonly IGhostTransaction _inner;
        private readonly ILogger _logger;
        private readonly IMetricsCollector _metrics;
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

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Mark as disposed
            if (_inner is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        public async Task CommitAsync(CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            using Activity? activity = StartActivity("GhostTransaction.CommitAsync");

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                await _inner.CommitAsync(ct);

                sw.Stop();
                await TrackSuccessMetricAsync("transaction_commit", sw.ElapsedMilliseconds);
                await TrackSuccessMetricAsync("transaction_duration", _transactionTimer.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                await TrackErrorMetricAsync("transaction_commit", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        public async Task RollbackAsync(CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            using Activity? activity = StartActivity("GhostTransaction.RollbackAsync");

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                await _inner.RollbackAsync(ct);

                sw.Stop();
                await TrackSuccessMetricAsync("transaction_rollback", sw.ElapsedMilliseconds);

                // Add a specific rollback event counter
                await _metrics.TrackMetricAsync(new MetricValue
                {
                        Name = "ghost.data.transaction_rollback.count",
                        Value = 1,
                        Tags = new Dictionary<string, string>
                        {
                                ["operation"] = "transaction_rollback"
                        },
                        Timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                await TrackErrorMetricAsync("transaction_rollback", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            using Activity? activity = StartActivity("GhostTransaction.QuerySingleAsync");
            activity?.SetTag("sql_hash", GetSqlHash(sql));
            activity?.SetTag("type", typeof(T).Name);
            activity?.SetTag("has_params", param != null);

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                T? result = await _inner.QuerySingleAsync<T>(sql, param, ct);

                sw.Stop();
                await TrackSuccessMetricAsync("transaction_query_single", sw.ElapsedMilliseconds);
                activity?.SetTag("found", result != null);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                await TrackErrorMetricAsync("transaction_query_single", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            using Activity? activity = StartActivity("GhostTransaction.QueryAsync");
            activity?.SetTag("sql_hash", GetSqlHash(sql));
            activity?.SetTag("type", typeof(T).Name);
            activity?.SetTag("has_params", param != null);

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                var result = await _inner.QueryAsync<T>(sql, param, ct);
                var resultList = result as ICollection<T> ?? result.ToList();

                sw.Stop();
                await TrackSuccessMetricAsync("transaction_query", sw.ElapsedMilliseconds);

                // Track result count as a separate metric
                await _metrics.TrackMetricAsync(new MetricValue
                {
                        Name = "ghost.data.transaction_query.result_count",
                        Value = resultList.Count,
                        Tags = new Dictionary<string, string>
                        {
                                ["operation"] = "transaction_query"
                        },
                        Timestamp = DateTime.UtcNow
                });

                activity?.SetTag("result_count", resultList.Count);

                return resultList;
            }
            catch (Exception ex)
            {
                sw.Stop();
                await TrackErrorMetricAsync("transaction_query", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            using Activity? activity = StartActivity("GhostTransaction.ExecuteAsync");
            activity?.SetTag("sql_hash", GetSqlHash(sql));
            activity?.SetTag("has_params", param != null);

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                int result = await _inner.ExecuteAsync(sql, param, ct);

                sw.Stop();
                await TrackSuccessMetricAsync("transaction_execute", sw.ElapsedMilliseconds);

                // Track rows affected as a separate metric
                await _metrics.TrackMetricAsync(new MetricValue
                {
                        Name = "ghost.data.transaction_execute.rows_affected",
                        Value = result,
                        Tags = new Dictionary<string, string>
                        {
                                ["operation"] = "transaction_execute"
                        },
                        Timestamp = DateTime.UtcNow
                });

                activity?.SetTag("rows_affected", result);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                await TrackErrorMetricAsync("transaction_execute", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            var commandsList = commands.ToList();
            using Activity? activity = StartActivity("GhostTransaction.ExecuteBatchAsync");
            activity?.SetTag("command_count", commandsList.Count);

            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                int result = await _inner.ExecuteBatchAsync(commandsList, ct);

                sw.Stop();
                await TrackSuccessMetricAsync("transaction_execute_batch", sw.ElapsedMilliseconds);

                // Track batch size and result as separate metrics
                await _metrics.TrackMetricAsync(new MetricValue
                {
                        Name = "ghost.data.transaction_execute_batch.size",
                        Value = commandsList.Count,
                        Tags = new Dictionary<string, string>
                        {
                                ["operation"] = "transaction_execute_batch"
                        },
                        Timestamp = DateTime.UtcNow
                });

                await _metrics.TrackMetricAsync(new MetricValue
                {
                        Name = "ghost.data.transaction_execute_batch.rows_affected",
                        Value = result,
                        Tags = new Dictionary<string, string>
                        {
                                ["operation"] = "transaction_execute_batch"
                        },
                        Timestamp = DateTime.UtcNow
                });

                activity?.SetTag("rows_affected", result);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                await TrackErrorMetricAsync("transaction_execute_batch", sw.ElapsedMilliseconds, ex);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
        }

        /// <summary>
        ///     Starts a new activity for tracing.
        /// </summary>
        /// <param name="name">The name of the activity.</param>
        /// <returns>The started activity.</returns>
        private static Activity? StartActivity(string name)
        {
            Activity activity = new Activity(name);
            activity.Start();
            return activity;
        }

        /// <summary>
        ///     Generates a simple hash for a SQL query for logging purposes.
        /// </summary>
        /// <param name="sql">The SQL query.</param>
        /// <returns>A hash code for the SQL query.</returns>
        private static string GetSqlHash(string sql)
        {
            if (string.IsNullOrEmpty(sql))
            {
                return "empty";
            }
            return Math.Abs(sql.GetHashCode()).ToString("X8");
        }

        /// <summary>
        ///     Tracks success metrics for an operation.
        /// </summary>
        /// <param name="operation">The name of the operation.</param>
        /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
        private async Task TrackSuccessMetricAsync(string operation, long elapsedMs)
        {
            // Track latency metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = $"ghost.data.{operation}.latency",
                    Value = elapsedMs,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = operation,
                            ["status"] = "success"
                    },
                    Timestamp = DateTime.UtcNow
            });

            // Track success count metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = $"ghost.data.{operation}.count",
                    Value = 1, // Increment by 1
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = operation,
                            ["status"] = "success"
                    },
                    Timestamp = DateTime.UtcNow
            });

            if (elapsedMs > 100)
            {
                _logger.LogDebug("Slow {Operation} operation: {ElapsedMs}ms",
                        operation, elapsedMs);
            }
        }

        /// <summary>
        ///     Tracks error metrics for an operation.
        /// </summary>
        /// <param name="operation">The name of the operation.</param>
        /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
        /// <param name="ex">The exception that occurred.</param>
        private async Task TrackErrorMetricAsync(string operation, long elapsedMs, Exception ex)
        {
            // Track latency metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = $"ghost.data.{operation}.latency",
                    Value = elapsedMs,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = operation,
                            ["status"] = "error"
                    },
                    Timestamp = DateTime.UtcNow
            });

            // Track error count metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = $"ghost.data.{operation}.count",
                    Value = 1, // Increment by 1
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = operation,
                            ["status"] = "error",
                            ["error_type"] = ex.GetType().Name
                    },
                    Timestamp = DateTime.UtcNow
            });

            _logger.LogError(ex, "{Operation} operation failed after {ElapsedMs}ms",
                    operation, elapsedMs);
        }

        /// <summary>
        ///     Throws if this object has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InstrumentedGhostTransaction));
            }
        }
    }

#endregion

#region Key-Value Operations

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.GetAsync");
        activity?.SetTag("key", key);
        activity?.SetTag("type", typeof(T).Name);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            T? result = await _inner.GetAsync<T>(key, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("get", sw.ElapsedMilliseconds);
            activity?.SetTag("cache_hit", result != null);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("get", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.SetAsync");
        activity?.SetTag("key", key);
        activity?.SetTag("type", typeof(T).Name);
        activity?.SetTag("expiry", expiry?.ToString() ?? "default");

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await _inner.SetAsync(key, value, expiry, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("set", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("set", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.DeleteAsync");
        activity?.SetTag("key", key);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            bool result = await _inner.DeleteAsync(key, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("delete", sw.ElapsedMilliseconds);
            activity?.SetTag("deleted", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("delete", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.ExistsAsync");
        activity?.SetTag("key", key);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            bool result = await _inner.ExistsAsync(key, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("exists", sw.ElapsedMilliseconds);
            activity?.SetTag("exists", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("exists", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        var keysList = keys.ToList();
        using Activity? activity = StartActivity("GhostData.GetBatchAsync");
        activity?.SetTag("key_count", keysList.Count);
        activity?.SetTag("type", typeof(T).Name);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.GetBatchAsync<T>(keysList, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("get_batch", sw.ElapsedMilliseconds);

            // Track batch size as a separate metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.get_batch.size",
                    Value = keysList.Count,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "get_batch"
                    },
                    Timestamp = DateTime.UtcNow
            });

            activity?.SetTag("hit_count", result.Count(kvp => kvp.Value != null));

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("get_batch", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.SetBatchAsync");
        activity?.SetTag("item_count", items.Count);
        activity?.SetTag("type", typeof(T).Name);
        activity?.SetTag("expiry", expiry?.ToString() ?? "default");

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            await _inner.SetBatchAsync(items, expiry, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("set_batch", sw.ElapsedMilliseconds);

            // Track batch size as a separate metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.set_batch.size",
                    Value = items.Count,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "set_batch"
                    },
                    Timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("set_batch", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        var keysList = keys.ToList();
        using Activity? activity = StartActivity("GhostData.DeleteBatchAsync");
        activity?.SetTag("key_count", keysList.Count);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            int result = await _inner.DeleteBatchAsync(keysList, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("delete_batch", sw.ElapsedMilliseconds);

            // Track batch size and result as separate metrics
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.delete_batch.size",
                    Value = keysList.Count,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "delete_batch"
                    },
                    Timestamp = DateTime.UtcNow
            });

            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.delete_batch.deleted_count",
                    Value = result,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "delete_batch"
                    },
                    Timestamp = DateTime.UtcNow
            });

            activity?.SetTag("deleted_count", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("delete_batch", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

#endregion

#region SQL Operations

    /// <inheritdoc />
    public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.QuerySingleAsync");
        activity?.SetTag("sql_hash", GetSqlHash(sql));
        activity?.SetTag("type", typeof(T).Name);
        activity?.SetTag("has_params", param != null);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            T? result = await _inner.QuerySingleAsync<T>(sql, param, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("query_single", sw.ElapsedMilliseconds);
            activity?.SetTag("found", result != null);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("query_single", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.QueryAsync");
        activity?.SetTag("sql_hash", GetSqlHash(sql));
        activity?.SetTag("type", typeof(T).Name);
        activity?.SetTag("has_params", param != null);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.QueryAsync<T>(sql, param, ct);
            var resultList = result as ICollection<T> ?? result.ToList();

            sw.Stop();
            await TrackSuccessMetricAsync("query", sw.ElapsedMilliseconds);

            // Track result count as a separate metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.query.result_count",
                    Value = resultList.Count,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "query"
                    },
                    Timestamp = DateTime.UtcNow
            });

            activity?.SetTag("result_count", resultList.Count);

            return resultList;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("query", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.ExecuteAsync");
        activity?.SetTag("sql_hash", GetSqlHash(sql));
        activity?.SetTag("has_params", param != null);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            int result = await _inner.ExecuteAsync(sql, param, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("execute", sw.ElapsedMilliseconds);

            // Track rows affected as a separate metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.execute.rows_affected",
                    Value = result,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "execute"
                    },
                    Timestamp = DateTime.UtcNow
            });

            activity?.SetTag("rows_affected", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("execute", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        var commandsList = commands.ToList();
        using Activity? activity = StartActivity("GhostData.ExecuteBatchAsync");
        activity?.SetTag("command_count", commandsList.Count);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            int result = await _inner.ExecuteBatchAsync(commandsList, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("execute_batch", sw.ElapsedMilliseconds);

            // Track batch size and result as separate metrics
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.execute_batch.size",
                    Value = commandsList.Count,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "execute_batch"
                    },
                    Timestamp = DateTime.UtcNow
            });

            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.execute_batch.rows_affected",
                    Value = result,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "execute_batch"
                    },
                    Timestamp = DateTime.UtcNow
            });

            activity?.SetTag("rows_affected", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("execute_batch", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

#endregion

#region Schema Operations

    /// <inheritdoc />
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.TableExistsAsync");
        activity?.SetTag("table_name", tableName);

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            bool result = await _inner.TableExistsAsync(tableName, ct);

            sw.Stop();
            await TrackSuccessMetricAsync("table_exists", sw.ElapsedMilliseconds);
            activity?.SetTag("exists", result);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("table_exists", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        using Activity? activity = StartActivity("GhostData.GetTableNamesAsync");

        Stopwatch sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.GetTableNamesAsync(ct);
            var resultList = result as ICollection<string> ?? result.ToList();

            sw.Stop();
            await TrackSuccessMetricAsync("get_table_names", sw.ElapsedMilliseconds);

            // Track table count as a separate metric
            await _metrics.TrackMetricAsync(new MetricValue
            {
                    Name = "ghost.data.get_table_names.table_count",
                    Value = resultList.Count,
                    Tags = new Dictionary<string, string>
                    {
                            ["operation"] = "get_table_names"
                    },
                    Timestamp = DateTime.UtcNow
            });

            activity?.SetTag("table_count", resultList.Count);

            return resultList;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TrackErrorMetricAsync("get_table_names", sw.ElapsedMilliseconds, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

#endregion

#region Helper Methods

    /// <summary>
    ///     Starts a new activity for tracing.
    /// </summary>
    /// <param name="name">The name of the activity.</param>
    /// <returns>The started activity.</returns>
    private static Activity? StartActivity(string name)
    {
        Activity activity = new Activity(name);
        activity.Start();
        return activity;
    }

    /// <summary>
    ///     Tracks success metrics for an operation.
    /// </summary>
    /// <param name="operation">The name of the operation.</param>
    /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
    private async Task TrackSuccessMetricAsync(string operation, long elapsedMs)
    {
        // Track latency metric
        await _metrics.TrackMetricAsync(new MetricValue
        {
                Name = $"ghost.data.{operation}.latency",
                Value = elapsedMs,
                Tags = new Dictionary<string, string>
                {
                        ["operation"] = operation,
                        ["status"] = "success"
                },
                Timestamp = DateTime.UtcNow
        });

        // Track success count metric
        await _metrics.TrackMetricAsync(new MetricValue
        {
                Name = $"ghost.data.{operation}.count",
                Value = 1, // Increment by 1
                Tags = new Dictionary<string, string>
                {
                        ["operation"] = operation,
                        ["status"] = "success"
                },
                Timestamp = DateTime.UtcNow
        });

        if (elapsedMs > 100)
        {
            _logger.LogDebug("Slow {Operation} operation: {ElapsedMs}ms",
                    operation, elapsedMs);
        }
    }

    /// <summary>
    ///     Tracks error metrics for an operation.
    /// </summary>
    /// <param name="operation">The name of the operation.</param>
    /// <param name="elapsedMs">The elapsed time in milliseconds.</param>
    /// <param name="ex">The exception that occurred.</param>
    private async Task TrackErrorMetricAsync(string operation, long elapsedMs, Exception ex)
    {
        // Track latency metric
        await _metrics.TrackMetricAsync(new MetricValue
        {
                Name = $"ghost.data.{operation}.latency",
                Value = elapsedMs,
                Tags = new Dictionary<string, string>
                {
                        ["operation"] = operation,
                        ["status"] = "error"
                },
                Timestamp = DateTime.UtcNow
        });

        // Track error count metric
        await _metrics.TrackMetricAsync(new MetricValue
        {
                Name = $"ghost.data.{operation}.count",
                Value = 1, // Increment by 1
                Tags = new Dictionary<string, string>
                {
                        ["operation"] = operation,
                        ["status"] = "error",
                        ["error_type"] = ex.GetType().Name
                },
                Timestamp = DateTime.UtcNow
        });

        _logger.LogError(ex, "{Operation} operation failed after {ElapsedMs}ms",
                operation, elapsedMs);
    }

    /// <summary>
    ///     Generates a simple hash for a SQL query for logging purposes.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>A hash code for the SQL query.</returns>
    private static string GetSqlHash(string sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return "empty";
        }
        return Math.Abs(sql.GetHashCode()).ToString("X8");
    }

    /// <summary>
    ///     Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(InstrumentedGhostData));
        }
    }

#endregion
}
