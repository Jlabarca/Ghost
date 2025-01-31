using Microsoft.Data.Sqlite;
using System.Data;
using Dapper;

namespace Ghost.Infrastructure.Storage.Database;

public class SQLiteClient : IDatabaseClient
{
    private readonly string _connectionString;
    private SqliteConnection _connection;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _disposed;

    public SQLiteClient(string connectionString)
    {
        _connectionString = connectionString;
    }

    private async Task<SqliteConnection> GetConnectionAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_connection?.State != ConnectionState.Open)
            {
                if (_connection != null)
                {
                    await _connection.DisposeAsync();
                }
                _connection = new SqliteConnection(_connectionString);
                await _connection.OpenAsync();
            }
            return _connection;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<T> QuerySingleAsync<T>(string sql, object param = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SQLiteClient));

        try
        {
            var connection = await GetConnectionAsync();
            return await connection.QuerySingleAsync<T>(sql, param);
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
        if (_disposed) throw new ObjectDisposedException(nameof(SQLiteClient));

        try
        {
            var connection = await GetConnectionAsync();
            return await connection.QueryAsync<T>(sql, param);
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
        if (_disposed) throw new ObjectDisposedException(nameof(SQLiteClient));

        try
        {
            var connection = await GetConnectionAsync();
            return await connection.ExecuteAsync(sql, param);
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
        if (_disposed) throw new ObjectDisposedException(nameof(SQLiteClient));

        try
        {
            var connection = await GetConnectionAsync();
            var transaction = await connection.BeginTransactionAsync();
            return new SQLiteTransactionWrapper(transaction as SqliteTransaction);
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