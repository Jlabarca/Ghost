using Ghost.Config;
using Ghost.Logging;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;
namespace Ghost.Pooling;

/// <summary>
///     Manages database connection pools for Ghost applications.
///     Supports Redis and PostgreSQL connection pooling.
/// </summary>
public sealed class ConnectionPoolManager : IAsyncDisposable
{
    private readonly GhostConfig _config;
    private readonly IGhostLogger _logger;
    private readonly Lazy<NpgsqlDataSource> _postgresDataSourceLazy;
    private readonly SemaphoreSlim _postgresSemaphore = new SemaphoreSlim(1, 1);

    private readonly Lazy<ConnectionMultiplexer> _redisConnectionLazy;

    private readonly SemaphoreSlim _redisSemaphore = new SemaphoreSlim(1, 1);

    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ConnectionPoolManager" /> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The configuration options.</param>
    public ConnectionPoolManager(
            IGhostLogger logger,
            GhostConfig config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));

        // Initialize Redis connection lazily
        _redisConnectionLazy = new Lazy<ConnectionMultiplexer>(() =>
        {
            _logger.LogInformation("Initializing Redis connection pool");
            ConfigurationOptions? connectionOptions = ConfigurationOptions.Parse(_config.Redis.ConnectionString);
            connectionOptions.AbortOnConnectFail = false;
            if (_config.Redis.ConnectRetry != null)
            {
                connectionOptions.ConnectRetry = _config.Redis.ConnectRetry.Value;
            }
            if (_config.Redis.ConnectTimeout != null)
            {
                connectionOptions.ConnectTimeout = (int)_config.Redis.ConnectTimeout.Value.TotalMilliseconds;
            }
            if (_config.Redis.SyncTimeout != null)
            {
                connectionOptions.SyncTimeout = (int)_config.Redis.SyncTimeout.Value.TotalMilliseconds;
            }

            ConnectionMultiplexer? connection = ConnectionMultiplexer.Connect(connectionOptions);
            connection.ConnectionFailed += (sender, args) =>
            {
                _logger.LogError("Redis connection failed: {EndPoint}, {FailureType}",
                        args.EndPoint, args.FailureType);
            };
            connection.ConnectionRestored += (sender, args) =>
            {
                _logger.LogInformation("Redis connection restored: {EndPoint}", args.EndPoint);
            };

            return connection;
        });

        // Initialize PostgreSQL connection pool lazily
        _postgresDataSourceLazy = new Lazy<NpgsqlDataSource>(() =>
        {
            if (string.IsNullOrEmpty(_config.Postgres.ConnectionString))
            {
                throw new InvalidOperationException("PostgreSQL connection string is not configured");
            }

            _logger.LogInformation("Initializing PostgreSQL connection pool");
            NpgsqlDataSourceBuilder? builder = new NpgsqlDataSourceBuilder(_config.Postgres.ConnectionString);

            // Configure connection pool
            builder.UseLoggerFactory(LoggerFactory.Create(builder =>
                    builder.AddProvider(new DbLoggerProvider(_logger))));


            NpgsqlConnectionStringBuilder? connStringBuilder = new NpgsqlConnectionStringBuilder(_config.Postgres.ConnectionString)
            {
                    MaxPoolSize = _config.Postgres.MaxPoolSize ?? 100,
                    MinPoolSize = _config.Postgres.MinPoolSize ?? 5,
                    CommandTimeout = _config.Postgres.CommandTimeout ?? 30
            };

            // Update the connection string in the builder
            builder = new NpgsqlDataSourceBuilder(connStringBuilder.ConnectionString);

            // Re-add the logger factory since we recreated the builder
            builder.UseLoggerFactory(LoggerFactory.Create(builder =>
                    builder.AddProvider(new DbLoggerProvider(_logger))));

            NpgsqlDataSource? dataSource = builder.Build();

            // Prewarm the connection pool if configured
            //var prewarmConnections = _config.Postgres.PrewarmConnections ?? false;
            // TODO: check if prewarmConnections is duplicated or wtf
            PrewarmPostgresConnectionPool(dataSource).GetAwaiter().GetResult();

            return dataSource;
        });
    }

    /// <summary>
    ///     Disposes the connection pool manager.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_redisConnectionLazy.IsValueCreated)
        {
            try
            {
                await _redisConnectionLazy.Value.CloseAsync();
                _redisConnectionLazy.Value.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing Redis connection");
            }
        }

        if (_postgresDataSourceLazy.IsValueCreated)
        {
            try
            {
                // In newer Npgsql versions, the data source implements IAsyncDisposable
                await _postgresDataSourceLazy.Value.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing PostgreSQL data source");
            }
        }

        _redisSemaphore.Dispose();
        _postgresSemaphore.Dispose();
    }

    /// <summary>
    ///     Gets a Redis database instance from the connection pooG.
    /// </summary>
    /// <param name="db">The database number to use.</param>
    /// <returns>A Redis database instance.</returns>
    public async Task<IDatabase> GetRedisDatabaseAsync(int db = -1)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConnectionPoolManager));
        }

        await _redisSemaphore.WaitAsync();
        try
        {
            return _redisConnectionLazy.Value.GetDatabase(db);
        }
        finally
        {
            _redisSemaphore.Release();
        }
    }

    /// <summary>
    ///     Gets a Redis subscriber instance from the connection pooG.
    /// </summary>
    /// <returns>A Redis subscriber instance.</returns>
    public async Task<ISubscriber> GetRedisSubscriberAsync()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConnectionPoolManager));
        }

        await _redisSemaphore.WaitAsync();
        try
        {
            return _redisConnectionLazy.Value.GetSubscriber();
        }
        finally
        {
            _redisSemaphore.Release();
        }
    }

    /// <summary>
    ///     Gets a PostgreSQL connection from the connection pooG.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A PostgreSQL connection.</returns>
    public async Task<NpgsqlConnection> GetPostgresConnectionAsync(CancellationToken ct = default(CancellationToken))
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConnectionPoolManager));
        }

        await _postgresSemaphore.WaitAsync(ct);
        try
        {
            NpgsqlConnection connection = _postgresDataSourceLazy.Value.CreateConnection();
            await connection.OpenAsync(ct);
            return connection;
        }
        finally
        {
            _postgresSemaphore.Release();
        }
    }

    /// <summary>
    ///     Gets the PostgreSQL data source.
    /// </summary>
    /// <returns>The PostgreSQL data source.</returns>
    public NpgsqlDataSource GetPostgresDataSource()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ConnectionPoolManager));
        }
        return _postgresDataSourceLazy.Value;
    }

    /// <summary>
    ///     Prewarms the PostgreSQL connection pool by opening connections.
    /// </summary>
    /// <param name="dataSource">The data source to prewarm.</param>
    private async Task PrewarmPostgresConnectionPool(NpgsqlDataSource dataSource)
    {
        int size = _config.Postgres.MinPoolSize ?? 5;
        var connections = new NpgsqlConnection[size];
        try
        {
            _logger.LogInformation("Prewarming PostgreSQL connection pool with {Count} connections",
                    _config.Postgres.MinPoolSize);

            for (int i = 0; i < _config.Postgres.MinPoolSize; i++)
            {
                connections[i] = dataSource.CreateConnection();
                await connections[i].OpenAsync();

                // Execute a simple query to ensure the connection is fully initialized
                using NpgsqlCommand? cmd = connections[i].CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
            }

            _logger.LogInformation("PostgreSQL connection pool prewarmed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prewarm PostgreSQL connection pool");
        }
        finally
        {
            // Close all connections to return them to the pool
            foreach (NpgsqlConnection? connection in connections)
            {
                if (connection != null)
                {
                    await connection.CloseAsync();
                    await connection.DisposeAsync();
                }
            }
        }
    }

    /// <summary>
    ///     A custom database logger provider.
    /// </summary>
    private class DbLoggerProvider : ILoggerProvider
    {
        private readonly ILogger _parentLogger;

        public DbLoggerProvider(ILogger parentLogger)
        {
            _parentLogger = parentLogger;
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new DbLogger(_parentLogger);
        }

        public void Dispose()
        {
        }

        private class DbLogger : ILogger
        {
            private readonly ILogger _parentLogger;

            public DbLogger(ILogger parentLogger)
            {
                _parentLogger = parentLogger;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                return null;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return _parentLogger.IsEnabled(logLevel);
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _parentLogger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
