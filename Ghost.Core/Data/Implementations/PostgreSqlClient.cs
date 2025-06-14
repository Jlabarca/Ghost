using Dapper;
using Npgsql;
namespace Ghost.Data.Implementations;

/// <summary>
///     PostgreSQL implementation of the database client.
/// </summary>
public class PostgreSqlClient : IDatabaseClient
{
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PostgreSqlClient" /> class.
    /// </summary>
    /// <param name="connectionString">The connection string to the PostgreSQL database.</param>
    /// <param name="logger">The logger.</param>
    public PostgreSqlClient(string connectionString)
    {
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    /// <summary>
    ///     Gets the connection string being used by this client.
    /// </summary>
    public string ConnectionString
    {
        get;
    }

    /// <inheritdoc />
    public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        await using NpgsqlConnection connection = await CreateAndOpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<T>(
                new CommandDefinition(sql, param, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        await using NpgsqlConnection connection = await CreateAndOpenConnectionAsync(ct);
        return await connection.QueryAsync<T>(
                new CommandDefinition(sql, param, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        await using NpgsqlConnection connection = await CreateAndOpenConnectionAsync(ct);
        return await connection.ExecuteAsync(
                new CommandDefinition(sql, param, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        NpgsqlConnection connection = await CreateAndOpenConnectionAsync(ct);
        NpgsqlTransaction transaction = await connection.BeginTransactionAsync(ct);

        return new PostgreSqlTransaction(connection, transaction);
    }

    /// <inheritdoc />
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // PostgreSQL-specific query to check if a table exists
        const string sql = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables
                    WHERE table_schema = 'public'
                    AND table_name = @TableName
                );";

        await using NpgsqlConnection connection = await CreateAndOpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<bool>(
                new CommandDefinition(sql, new
                {
                        TableName = tableName
                }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // PostgreSQL-specific query to get all table names
        const string sql = @"
                SELECT table_name
                FROM information_schema.tables
                WHERE table_schema = 'public'
                ORDER BY table_name;";

        await using NpgsqlConnection connection = await CreateAndOpenConnectionAsync(ct);
        return await connection.QueryAsync<string>(
                new CommandDefinition(sql, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default(CancellationToken))
    {
        try
        {
            await using NpgsqlConnection connection = await CreateAndOpenConnectionAsync(ct);
            // Simple query to test connection
            await connection.ExecuteScalarAsync<int>("SELECT 1", ct);
            return true;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to connect to PostgreSQL database");
            return false;
        }
    }

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

            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Creates and opens a new connection to the database.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An open connection to the database.</returns>
    private async Task<NpgsqlConnection> CreateAndOpenConnectionAsync(CancellationToken ct = default(CancellationToken))
    {
        NpgsqlConnection connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>
    ///     Throws an ObjectDisposedException if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PostgreSqlClient));
        }
    }

    /// <summary>
    ///     Represents a PostgreSQL transaction.
    /// </summary>
    private class PostgreSqlTransaction : IGhostTransaction
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private bool _committed;
        private bool _disposed;

        public PostgreSqlTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        }

        /// <inheritdoc />
        public async Task CommitAsync(CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();
            await _transaction.CommitAsync(ct);
            _committed = true;
        }

        /// <inheritdoc />
        public async Task RollbackAsync(CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            if (!_committed)
            {
                await _transaction.RollbackAsync(ct);
            }
        }

        /// <inheritdoc />
        public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();
            return await _connection.QuerySingleOrDefaultAsync<T>(
                    new CommandDefinition(sql, param, _transaction, cancellationToken: ct));
        }

        /// <inheritdoc />
        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();
            return await _connection.QueryAsync<T>(
                    new CommandDefinition(sql, param, _transaction, cancellationToken: ct));
        }

        /// <inheritdoc />
        public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();
            return await _connection.ExecuteAsync(
                    new CommandDefinition(sql, param, _transaction, cancellationToken: ct));
        }

        /// <inheritdoc />
        public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken))
        {
            ThrowIfDisposed();

            int totalAffected = 0;

            foreach ((string sql, object? param) in commands)
            {
                int affected = await _connection.ExecuteAsync(
                        new CommandDefinition(sql, param, _transaction, cancellationToken: ct));
                totalAffected += affected;
            }

            return totalAffected;
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
                try
                {
                    await _transaction.RollbackAsync();
                }
                catch
                {
                    // Ignore rollback errors on disposal
                }
            }

            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }

        /// <summary>
        ///     Throws an ObjectDisposedException if this object has been disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PostgreSqlTransaction));
            }
        }
    }
}
