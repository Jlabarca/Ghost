using System.Net.Sockets;
using Ghost.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using StackExchange.Redis;

namespace Ghost.Core.Data.Decorators;

/// <summary>
/// Decorator that adds resilience patterns to IGhostData operations.
/// Implements retry and circuit breaker patterns for data operations.
/// </summary>
public class ResilientGhostData : IGhostData
{
    private readonly IGhostData _inner;
    private readonly ILogger<ResilientGhostData> _logger;
    private readonly IOptions<ResilienceConfiguration> _config;

    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly AsyncCircuitBreakerPolicy _circuitBreakerPolicy;
    private readonly AsyncPolicy _combinedPolicy;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilientGhostData"/> class.
    /// </summary>
    /// <param name="inner">The decorated IGhostData implementation.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The resilience configuration.</param>
    public ResilientGhostData(
            IGhostData inner,
            ILogger<ResilientGhostData> logger,
            IOptions<ResilienceConfiguration> config)
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
    public IDatabaseClient GetDatabaseClient() => _inner.GetDatabaseClient();

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
                        _config.Value.RetryCount,
                        retryAttempt => TimeSpan.FromMilliseconds(_config.Value.RetryBaseDelayMs * Math.Pow(2, retryAttempt - 1)), // Exponential backoff
                        (exception, timeSpan, retryCount, context) =>
                        {
                            _logger.LogWarning(exception,
                                    "Retry {RetryCount} of {MaxRetry} after {Delay}ms for operation {OperationKey}",
                                    retryCount, _config.Value.RetryCount, timeSpan.TotalMilliseconds,
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
                        _config.Value.CircuitBreakerThreshold,
                        TimeSpan.FromMilliseconds(_config.Value.CircuitBreakerDurationMs),
                        onBreak: (exception, timespan, context) =>
                        {
                            _logger.LogError(exception,
                                    "Circuit breaker opened for {Duration}ms after {Failures} failures. Operation: {OperationKey}",
                                    timespan.TotalMilliseconds, _config.Value.CircuitBreakerThreshold,
                                    context.ContainsKey("OperationKey") ? context["OperationKey"] : "Unknown");
                        },
                        onReset: context =>
                        {
                            _logger.LogInformation(
                                    "Circuit breaker reset for operation: {OperationKey}",
                                    context.ContainsKey("OperationKey") ? context["OperationKey"] : "Unknown");
                        },
                        onHalfOpen: () =>
                        {
                            _logger.LogInformation("Circuit breaker is half-open, next call is a trial");
                        });
    }

        #endregion

        #region Key-Value Operations

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.GetAsync<T>(key, token);
        }, new Context { ["OperationKey"] = $"GetAsync:{key}" }, ct);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            await _inner.SetAsync(key, value, expiry, token);
            return true; // Needed for generic policy execution
        }, new Context { ["OperationKey"] = $"SetAsync:{key}" }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.DeleteAsync(key, token);
        }, new Context { ["OperationKey"] = $"DeleteAsync:{key}" }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.ExistsAsync(key, token);
        }, new Context { ["OperationKey"] = $"ExistsAsync:{key}" }, ct);
    }

        #endregion

        #region Batch Key-Value Operations

    /// <inheritdoc />
    public async Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.GetBatchAsync<T>(keys, token);
        }, new Context { ["OperationKey"] = "GetBatchAsync" }, ct);
    }

    /// <inheritdoc />
    public async Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            await _inner.SetBatchAsync(items, expiry, token);
            return true; // Needed for generic policy execution
        }, new Context { ["OperationKey"] = "SetBatchAsync" }, ct);
    }

    /// <inheritdoc />
    public async Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.DeleteBatchAsync(keys, token);
        }, new Context { ["OperationKey"] = "DeleteBatchAsync" }, ct);
    }

        #endregion

        #region SQL Operations

    /// <inheritdoc />
    public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.QuerySingleAsync<T>(sql, param, token);
        }, new Context { ["OperationKey"] = $"QuerySingleAsync:{GetSqlHash(sql)}" }, ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.QueryAsync<T>(sql, param, token);
        }, new Context { ["OperationKey"] = $"QueryAsync:{GetSqlHash(sql)}" }, ct);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.ExecuteAsync(sql, param, token);
        }, new Context { ["OperationKey"] = $"ExecuteAsync:{GetSqlHash(sql)}" }, ct);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.ExecuteBatchAsync(commands, token);
        }, new Context { ["OperationKey"] = "ExecuteBatchAsync" }, ct);
    }

        #endregion

        #region Transaction Support

    /// <inheritdoc />
    public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            var transaction = await _inner.BeginTransactionAsync(token);
            return new ResilientGhostTransaction(transaction, _combinedPolicy, _logger);
        }, new Context { ["OperationKey"] = "BeginTransactionAsync" }, ct);
    }

        #endregion

        #region Schema Operations

    /// <inheritdoc />
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.TableExistsAsync(tableName, token);
        }, new Context { ["OperationKey"] = $"TableExistsAsync:{tableName}" }, ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        return await _combinedPolicy.ExecuteAsync(async (context, token) =>
        {
            return await _inner.GetTableNamesAsync(token);
        }, new Context { ["OperationKey"] = "GetTableNamesAsync" }, ct);
    }

        #endregion

        #region Helper Methods

    /// <summary>
    /// Generates a simple hash for a SQL query for logging purposes.
    /// </summary>
    /// <param name="sql">The SQL query.</param>
    /// <returns>A hash code for the SQL query.</returns>
    private static string GetSqlHash(string sql)
    {
        if (string.IsNullOrEmpty(sql)) return "empty";

        // Create a simple hash for the SQL query (for logging only)
        return Math.Abs(sql.GetHashCode()).ToString("X8");
    }

    /// <summary>
    /// Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ResilientGhostData));
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

        #region Resilient Transaction Implementation

    /// <summary>
    /// Wraps a transaction with resilience policies.
    /// </summary>
    private class ResilientGhostTransaction : IGhostTransaction
    {
        private readonly IGhostTransaction _inner;
        private readonly AsyncPolicy _policy;
        private readonly ILogger _logger;
        private bool _disposed;

        public ResilientGhostTransaction(IGhostTransaction inner, AsyncPolicy policy, ILogger logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task CommitAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            await _policy.ExecuteAsync(async (context, token) =>
            {
                await _inner.CommitAsync(token);
                return true; // Needed for generic policy execution
            }, new Context { ["OperationKey"] = "Transaction:Commit" }, ct);
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            try
            {
                await _policy.ExecuteAsync(async (context, token) =>
                {
                    await _inner.RollbackAsync(token);
                    return true; // Needed for generic policy execution
                }, new Context { ["OperationKey"] = "Transaction:Rollback" }, ct);
            }
            catch (Exception ex)
            {
                // Log but don't rethrow - rollback failures are expected in some cases
                _logger.LogWarning(ex, "Error rolling back transaction");
            }
        }

        public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            return await _policy.ExecuteAsync(async (context, token) =>
            {
                return await _inner.QuerySingleAsync<T>(sql, param, token);
            }, new Context { ["OperationKey"] = $"Transaction:QuerySingle:{GetSqlHash(sql)}" }, ct);
        }

        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            return await _policy.ExecuteAsync(async (context, token) =>
            {
                return await _inner.QueryAsync<T>(sql, param, token);
            }, new Context { ["OperationKey"] = $"Transaction:Query:{GetSqlHash(sql)}" }, ct);
        }

        public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            return await _policy.ExecuteAsync(async (context, token) =>
            {
                return await _inner.ExecuteAsync(sql, param, token);
            }, new Context { ["OperationKey"] = $"Transaction:Execute:{GetSqlHash(sql)}" }, ct);
        }

        public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            return await _policy.ExecuteAsync(async (context, token) =>
            {
                return await _inner.ExecuteBatchAsync(commands, token);
            }, new Context { ["OperationKey"] = "Transaction:ExecuteBatch" }, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                // Try to roll back if not explicitly committed
                await RollbackAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error during transaction automatic rollback on dispose");
            }

            await _inner.DisposeAsync();
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
                throw new ObjectDisposedException(nameof(ResilientGhostTransaction));
        }
    }

        #endregion
}