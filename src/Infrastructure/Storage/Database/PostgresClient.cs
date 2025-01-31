using Npgsql;
using Dapper;
using Ghost.Infrastructure.Storage.Database;
using System.Data;

namespace Ghost.Infrastructure.Storage;

public class PostgresClient : IDatabaseClient
{
    private readonly string _connectionString;
    private NpgsqlConnection _connection;
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    public PostgresClient(string connectionString)
    {
        _connectionString = connectionString;
        _semaphore = new SemaphoreSlim(1, 1);
    }

    private async Task EnsureConnectionAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));

        await _semaphore.WaitAsync();
        try
        {
            if (_connection?.State != ConnectionState.Open)
            {
                if (_connection != null)
                {
                    await _connection.DisposeAsync();
                }
                _connection = new NpgsqlConnection(_connectionString);
                await _connection.OpenAsync();
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<T> QuerySingleAsync<T>(string sql, object param = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));

        try
        {
            await EnsureConnectionAsync();
            return await _connection.QuerySingleAsync<T>(sql, param);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to execute single query",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));

        try
        {
            await EnsureConnectionAsync();
            return await _connection.QueryAsync<T>(sql, param);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to execute query",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<int> ExecuteAsync(string sql, object param = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));

        try
        {
            await EnsureConnectionAsync();
            return await _connection.ExecuteAsync(sql, param);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to execute command",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<IGhostTransaction> BeginTransactionAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));

        try
        {
            await EnsureConnectionAsync();
            var transaction = await _connection.BeginTransactionAsync();
            return new PostgresTransactionWrapper(transaction);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to begin transaction",
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
            _disposed = true;

            if (_connection != null)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}

public class PostgresTransactionWrapper : IGhostTransaction
{
    private readonly NpgsqlTransaction _transaction;
    private bool _disposed;

    public PostgresTransactionWrapper(NpgsqlTransaction transaction)
    {
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PostgresTransactionWrapper));

        try
        {
            await _transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to commit transaction",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PostgresTransactionWrapper));

        try
        {
            await _transaction.RollbackAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new GhostException(
                "Failed to rollback transaction",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await _transaction.DisposeAsync();
            _disposed = true;
        }
    }
}