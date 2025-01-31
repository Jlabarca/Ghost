using Npgsql;
using Dapper;
using System.Data;

namespace Ghost.Infrastructure.Storage;

public interface IPostgresClient : IAsyncDisposable
{
    Task<T> QuerySingleAsync<T>(string sql, object param = null);
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null);
    Task<int> ExecuteAsync(string sql, object param = null);
    Task<NpgsqlTransaction> BeginTransactionAsync();
}

public class PostgresClient : IPostgresClient
{
    private readonly string _connectionString;
    private NpgsqlConnection _connection;
    private readonly SemaphoreSlim _semaphore;

    public PostgresClient(string connectionString)
    {
        _connectionString = connectionString;
        _semaphore = new SemaphoreSlim(1, 1);
    }

    private async Task EnsureConnectionAsync()
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

    public async Task<NpgsqlTransaction> BeginTransactionAsync()
    {
        try
        {
            await EnsureConnectionAsync();
            return await _connection.BeginTransactionAsync();
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
        await _semaphore.WaitAsync();
        try
        {
            if (_connection != null)
            {
                await _connection.DisposeAsync();
            }
        }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}
