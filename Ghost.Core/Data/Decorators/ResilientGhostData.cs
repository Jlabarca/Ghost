using System.Net.Sockets;
using Ghost.Config;
using Ghost.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using StackExchange.Redis;
namespace Ghost.Data.Decorators;

/// <summary>
///     Decorator that adds resilience patterns to IGhostData operations.
///     Implements retry and circuit breaker patterns for data operations.
/// </summary>
public class ResilientGhostData : IGhostData
{
    private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy;
    private readonly AsyncPolicy _combinedPolicy;
    private readonly IOptions<ResilienceDataConfig> _config;
    private readonly IGhostData _inner;
    private readonly IGhostLogger _logger;

    private readonly AsyncRetryPolicy _retryPolicy;

    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ResilientGhostData" /> class.
    /// </summary>
    /// <param name="inner">The decorated IGhostData implementation.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The resilience configuration.</param>
    public ResilientGhostData(
            IGhostData inner,
            IGhostLogger logger,
            IOptions<ResilienceDataConfig> config)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        // Create retry policy
        _retryPolicy = CreateRetryPolicy();

        // Create circuit breaker policy
        _circuitBreakerPolicy = CreateCircuitBreakerPolicy();

        // Combine policies: apply retry first, then circuit breaker
        _combinedPolicy = Policy.WrapAsync(_circuitBreakerPolicy, _retryPolicy);
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

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            IGhostTransaction transaction = await _inner.BeginTransactionAsync(token);
            return new ResilientGhostTransaction(transaction, _combinedPolicy, _logger);
        }, new Context
        {
                ["OperationKey"] = "BeginTransactionAsync"
        }, ct);
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

#region Resilient Transaction Implementation

    /// <summary>
    ///     Wraps a transaction with resilience policies.
    /// </summary>
    /// <summary>
    ///     Wraps a transaction with resilience policies.
    /// </summary>
    private class ResilientGhostTransaction : IGhostTransaction
    {
        private readonly IGhostTransaction _inner;
        private readonly ILogger _logger;
        private readonly AsyncPolicy _policy;
        private volatile bool _committed;
        private volatile bool _disposed;
        private volatile bool _rolledBack;

        public ResilientGhostTransaction(IGhostTransaction inner, AsyncPolicy policy, ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task CommitAsync(CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            if (_committed || _rolledBack)
            {
                throw new InvalidOperationException("Transaction has already been committed or rolled back");
            }

            await _policy.ExecuteAsync(async (context, token) =>
            {
                await _inner.CommitAsync(token);
                _committed = true;
                return true; // Needed for generic policy execution
            }, new Context
            {
                    ["OperationKey"] = "Transaction:Commit"
            }, ct);
        }

        public async Task RollbackAsync(CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            if (_committed || _rolledBack)
            {
                return; // Already handled
            }

            try
            {
                await _policy.ExecuteAsync(async (context, token) =>
                {
                    await _inner.RollbackAsync(token);
                    _rolledBack = true;
                    return true; // Needed for generic policy execution
                }, new Context
                {
                        ["OperationKey"] = "Transaction:Rollback"
                }, ct);
            }
            catch (Exception ex)
            {
                // Log but don't rethrow - rollback failures are expected in some cases
                _logger.LogDebug(ex, "Error rolling back transaction");
                _rolledBack = true; // Mark as rolled back even if it failed
            }
        }

        public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            if (_committed || _rolledBack)
            {
                throw new InvalidOperationException("Cannot execute operations on a committed or rolled back transaction");
            }

            return await _policy.ExecuteAsync(async (context, token) =>
            {
                return await _inner.QuerySingleAsync<T>(sql, param, token);
            }, new Context
            {
                    ["OperationKey"] = $"Transaction:QuerySingle:{GetSqlHash(sql)}"
            }, ct);
        }

        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            if (_committed || _rolledBack)
            {
                throw new InvalidOperationException("Cannot execute operations on a committed or rolled back transaction");
            }

            return await _policy.ExecuteAsync(async (context, token) =>
            {
                return await _inner.QueryAsync<T>(sql, param, token);
            }, new Context
            {
                    ["OperationKey"] = $"Transaction:Query:{GetSqlHash(sql)}"
            }, ct);
        }

        public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            if (_committed || _rolledBack)
            {
                throw new InvalidOperationException("Cannot execute operations on a committed or rolled back transaction");
            }

            return await _policy.ExecuteAsync(async (context, token) =>
            {
                return await _inner.ExecuteAsync(sql, param, token);
            }, new Context
            {
                    ["OperationKey"] = $"Transaction:Execute:{GetSqlHash(sql)}"
            }, ct);
        }

        public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            if (_committed || _rolledBack)
            {
                throw new InvalidOperationException("Cannot execute operations on a committed or rolled back transaction");
            }

            return await _policy.ExecuteAsync(async (context, token) =>
            {
                return await _inner.ExecuteBatchAsync(commands, token);
            }, new Context
            {
                    ["OperationKey"] = "Transaction:ExecuteBatch"
            }, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                // Only try to roll back if we haven't committed or rolled back yet
                if (!_committed && !_rolledBack)
                {
                    using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // Timeout for disposal
                    await RollbackAsync(cts.Token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during transaction automatic rollback on dispose");
            }

            try
            {
                await _inner.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing inner transaction");
            }
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
                throw new ObjectDisposedException(nameof(ResilientGhostTransaction));
            }
        }
    }

#endregion

#region Policy Creation

    private AsyncRetryPolicy CreateRetryPolicy()
    {
        return Policy
                .Handle<RedisConnectionException>()
                .Or<RedisTimeoutException>()
                .Or<SocketException>()
                .Or<NpgsqlException>()
                .Or<TimeoutException>()
                .WaitAndRetryAsync(
                        _config.Value.RetryCount ?? 3, // Handle nullable int with default value
                        retryAttempt => TimeSpan.FromMilliseconds((_config.Value.RetryBaseDelayMs ?? 100) * Math.Pow(2, retryAttempt - 1)), // Actual exponential backoff
                        (exception, timeSpan, retryCount, context) =>
                        {
                            _logger.LogWarning(exception,
                                    "Retry {RetryCount} of {MaxRetry} after {Delay}ms for operation {OperationKey}",
                                    retryCount, _config.Value.RetryCount ?? 3, timeSpan.TotalMilliseconds,
                                    context.ContainsKey("OperationKey") ? context["OperationKey"] : "Unknown");
                        });
    }

    private AsyncCircuitBreakerPolicy CreateCircuitBreakerPolicy()
    {
        return Policy
                .Handle<RedisConnectionException>()
                .Or<RedisTimeoutException>()
                .Or<SocketException>()
                .Or<NpgsqlException>()
                .Or<TimeoutException>()
                .CircuitBreakerAsync(
                        _config.Value.CircuitBreakerThreshold.GetValueOrDefault(5), // Default to 5 failures if not set
                        TimeSpan.FromMilliseconds(_config.Value.CircuitBreakerDurationMs.GetValueOrDefault(10000)), // Default to 10 seconds if not set
                        (exception, timespan, context) =>
                        {
                            _logger.LogError(exception,
                                    "Circuit breaker opened for {Duration}ms after {Failures} failures. Operation: {OperationKey}",
                                    timespan.TotalMilliseconds, _config.Value.CircuitBreakerThreshold,
                                    context.ContainsKey("OperationKey") ? context["OperationKey"] : "Unknown");
                        },
                        context =>
                        {
                            _logger.LogInformation(
                                    "Circuit breaker reset for operation: {OperationKey}",
                                    context.ContainsKey("OperationKey") ? context["OperationKey"] : "Unknown");
                        },
                        () =>
                        {
                            _logger.LogInformation("Circuit breaker is half-open, next call is a trial");
                        });
    }

#endregion

#region Key-Value Operations

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.GetAsync<T>(key, token);
        }, new Context
        {
                ["OperationKey"] = $"GetAsync:{key}"
        }, ct);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            await _inner.SetAsync(key, value, expiry, token);
            return true; // Needed for generic policy execution
        }, new Context
        {
                ["OperationKey"] = $"SetAsync:{key}"
        }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.DeleteAsync(key, token);
        }, new Context
        {
                ["OperationKey"] = $"DeleteAsync:{key}"
        }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.ExistsAsync(key, token);
        }, new Context
        {
                ["OperationKey"] = $"ExistsAsync:{key}"
        }, ct);
    }

#endregion

#region Batch Key-Value Operations

    /// <inheritdoc />
    public async Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.GetBatchAsync<T>(keys, token);
        }, new Context
        {
                ["OperationKey"] = "GetBatchAsync"
        }, ct);
    }

    /// <inheritdoc />
    public async Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            await _inner.SetBatchAsync(items, expiry, token);
            return true; // Needed for generic policy execution
        }, new Context
        {
                ["OperationKey"] = "SetBatchAsync"
        }, ct);
    }

    /// <inheritdoc />
    public async Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.DeleteBatchAsync(keys, token);
        }, new Context
        {
                ["OperationKey"] = "DeleteBatchAsync"
        }, ct);
    }

#endregion

#region SQL Operations

    /// <inheritdoc />
    public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.QuerySingleAsync<T>(sql, param, token);
        }, new Context
        {
                ["OperationKey"] = $"QuerySingleAsync:{GetSqlHash(sql)}"
        }, ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.QueryAsync<T>(sql, param, token);
        }, new Context
        {
                ["OperationKey"] = $"QueryAsync:{GetSqlHash(sql)}"
        }, ct);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.ExecuteAsync(sql, param, token);
        }, new Context
        {
                ["OperationKey"] = $"ExecuteAsync:{GetSqlHash(sql)}"
        }, ct);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.ExecuteBatchAsync(commands, token);
        }, new Context
        {
                ["OperationKey"] = "ExecuteBatchAsync"
        }, ct);
    }

#endregion

#region Schema Operations

    /// <inheritdoc />
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.TableExistsAsync(tableName, token);
        }, new Context
        {
                ["OperationKey"] = $"TableExistsAsync:{tableName}"
        }, ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.GetTableNamesAsync(token);
        }, new Context
        {
                ["OperationKey"] = "GetTableNamesAsync"
        }, ct);
    }

#endregion

#region Helper Methods

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

        // Create a simple hash for the SQL query (for logging only)
        return Math.Abs(sql.GetHashCode()).ToString("X8");
    }

    /// <summary>
    ///     Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ResilientGhostData));
        }
    }

#endregion
}
