using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Ghost.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Ghost.Core.Pooling
{
    /// <summary>
    /// Manages database connection pools for Ghost applications.
    /// Supports Redis and PostgreSQL connection pooling.
    /// </summary>
    public sealed class ConnectionPoolManager : IAsyncDisposable
    {
        private readonly ILogger<ConnectionPoolManager> _logger;
        private readonly GhostConfiguration _config;
        
        private readonly Lazy<ConnectionMultiplexer> _redisConnectionLazy;
        private readonly Lazy<NpgsqlDataSource> _postgresDataSourceLazy;
        
        private readonly SemaphoreSlim _redisSemaphore = new(1, 1);
        private readonly SemaphoreSlim _postgresSemaphore = new(1, 1);
        
        private bool _disposed;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionPoolManager"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="config">The configuration options.</param>
        public ConnectionPoolManager(
            ILogger<ConnectionPoolManager> logger,
            IOptions<GhostConfiguration> config)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            
            // Initialize Redis connection lazily
            _redisConnectionLazy = new Lazy<ConnectionMultiplexer>(() =>
            {
                _logger.LogInformation("Initializing Redis connection pool");
                var connectionOptions = ConfigurationOptions.Parse(_config.Redis.ConnectionString);
                connectionOptions.AbortOnConnectFail = false;
                connectionOptions.ConnectRetry = _config.Redis.ConnectRetry;
                connectionOptions.ConnectTimeout = (int)_config.Redis.ConnectTimeout.TotalMilliseconds;
                connectionOptions.SyncTimeout = (int)_config.Redis.SyncTimeout.TotalMilliseconds;
                
                var connection = ConnectionMultiplexer.Connect(connectionOptions);
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
                    throw new InvalidOperationException("PostgreSQL connection string is not configured");

                _logger.LogInformation("Initializing PostgreSQL connection pool");
                var builder = new NpgsqlDataSourceBuilder(_config.Postgres.ConnectionString);

                // Configure connection pool
                builder.UseLoggerFactory(LoggerFactory.Create(builder =>
                        builder.AddProvider(new DbLoggerProvider(_logger))));

                // Configure pool size and timeout using connection string builder
                var connStringBuilder = new NpgsqlConnectionStringBuilder(_config.Postgres.ConnectionString)
                {
                        MaxPoolSize = _config.Postgres.MaxPoolSize,
                        MinPoolSize = _config.Postgres.MinPoolSize
                };

                // Set command timeout if configured
                if (_config.Postgres.CommandTimeout > 0)
                {
                    connStringBuilder.CommandTimeout = _config.Postgres.CommandTimeout;
                }

                // Update the connection string in the builder
                builder = new NpgsqlDataSourceBuilder(connStringBuilder.ConnectionString);

                // Re-add the logger factory since we recreated the builder
                builder.UseLoggerFactory(LoggerFactory.Create(builder =>
                        builder.AddProvider(new DbLoggerProvider(_logger))));

                var dataSource = builder.Build();

                // Prewarm the connection pool if configured
                if (_config.Postgres.PrewarmConnections)
                {
                    PrewarmPostgresConnectionPool(dataSource).GetAwaiter().GetResult();
                }

                return dataSource;
            });
        }
        
        /// <summary>
        /// Gets a Redis database instance from the connection pooG.
        /// </summary>
        /// <param name="db">The database number to use.</param>
        /// <returns>A Redis database instance.</returns>
        public async Task<IDatabase> GetRedisDatabaseAsync(int db = -1)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPoolManager));
            
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
        /// Gets a Redis subscriber instance from the connection pooG.
        /// </summary>
        /// <returns>A Redis subscriber instance.</returns>
        public async Task<ISubscriber> GetRedisSubscriberAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPoolManager));
            
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
        /// Gets a PostgreSQL connection from the connection pooG.
        /// </summary>
        /// <param name="ct">Optional cancellation token.</param>
        /// <returns>A PostgreSQL connection.</returns>
        public async Task<NpgsqlConnection> GetPostgresConnectionAsync(CancellationToken ct = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPoolManager));
            
            await _postgresSemaphore.WaitAsync(ct);
            try
            {
                var connection = _postgresDataSourceLazy.Value.CreateConnection();
                await connection.OpenAsync(ct);
                return connection;
            }
            finally
            {
                _postgresSemaphore.Release();
            }
        }
        
        /// <summary>
        /// Gets the PostgreSQL data source.
        /// </summary>
        /// <returns>The PostgreSQL data source.</returns>
        public NpgsqlDataSource GetPostgresDataSource()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPoolManager));
            return _postgresDataSourceLazy.Value;
        }
        
        /// <summary>
        /// Prewarms the PostgreSQL connection pool by opening connections.
        /// </summary>
        /// <param name="dataSource">The data source to prewarm.</param>
        private async Task PrewarmPostgresConnectionPool(NpgsqlDataSource dataSource)
        {
            var connections = new NpgsqlConnection[_config.Postgres.MinPoolSize];
            try
            {
                _logger.LogInformation("Prewarming PostgreSQL connection pool with {Count} connections", 
                    _config.Postgres.MinPoolSize);
                
                for (var i = 0; i < _config.Postgres.MinPoolSize; i++)
                {
                    connections[i] = dataSource.CreateConnection();
                    await connections[i].OpenAsync();
                    
                    // Execute a simple query to ensure the connection is fully initialized
                    using var cmd = connections[i].CreateCommand();
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
                foreach (var connection in connections)
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
        /// Disposes the connection pool manager.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            
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
        /// A custom database logger provider.
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
}
