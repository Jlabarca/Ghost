using Dapper;
using Npgsql;
using System.Data;
using System.Text.Json;
namespace Ghost.Core.Data;

public class PostgresDatabase : IDatabaseClient
{
  private readonly NpgsqlConnection _connection;
  private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
  private bool _disposed;

  public PostgresDatabase(string connectionString)
  {
    _connection = new NpgsqlConnection(connectionString);
  }

  public DatabaseType DatabaseType
  {
    get
    {
      return DatabaseType.PostgreSQL;
    }
  }

  public async Task<bool> IsAvailableAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    try
    {
      NpgsqlConnection conn = await GetConnectionAsync(ct);
      return conn.State == ConnectionState.Open;
    }
    catch
    {
      return false;
    }
  }

  public async Task<long> GetStorageSizeAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            SELECT pg_database_size(current_database())";
    return await conn.ExecuteScalarAsync<long>(sql);
  }

  public async Task<T> GetValueAsync<T>(string key, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            SELECT value 
            FROM key_value_store 
            WHERE key = @key";

    string? result = await conn.QuerySingleOrDefaultAsync<string>(sql, new
    {
        key
    });
    return result != null ? JsonSerializer.Deserialize<T>(result) : default(T?);
  }

  public async Task<bool> SetValueAsync<T>(string key, T value, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            INSERT INTO key_value_store (key, value)
            VALUES (@key, @value)
            ON CONFLICT (key) DO UPDATE 
            SET value = @value";

    string serialized = JsonSerializer.Serialize(value);
    int result = await conn.ExecuteAsync(sql, new
    {
        key,
        value = serialized
    });
    return result > 0;
  }

  public async Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            DELETE FROM key_value_store 
            WHERE key = @key";

    int result = await conn.ExecuteAsync(sql, new
    {
        key
    });
    return result > 0;
  }

  public async Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            SELECT 1 
            FROM key_value_store 
            WHERE key = @key";

    int? result = await conn.QuerySingleOrDefaultAsync<int?>(sql, new
    {
        key
    });
    return result.HasValue;
  }

  public async Task<T> QuerySingleAsync<T>(string sql, object param = null, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    return await conn.QuerySingleAsync<T>(sql, param);
  }

  public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    return await conn.QueryAsync<T>(sql, param);
  }

  public async Task<int> ExecuteAsync(string sql, object param = null, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    return await conn.ExecuteAsync(sql, param);
  }

  public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    NpgsqlTransaction transaction = await conn.BeginTransactionAsync(ct);
    return new PostgresTransactionWrapper(transaction);
  }

  public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            SELECT 1 
            FROM information_schema.tables 
            WHERE table_schema = 'public' 
            AND table_name = @tableName";

    int? result = await conn.QuerySingleOrDefaultAsync<int?>(sql, new
    {
        tableName
    });
    return result.HasValue;
  }

  public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresDatabase));

    NpgsqlConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            SELECT table_name 
            FROM information_schema.tables 
            WHERE table_schema = 'public'";

    return await conn.QueryAsync<string>(sql);
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed) return;

    await _semaphore.WaitAsync();
    try
    {
      if (_disposed) return;
      _disposed = true;

      await _connection.DisposeAsync();
      _semaphore.Dispose();
    }
    finally
    {
      _semaphore.Release();
    }
  }

  internal async Task<NpgsqlConnection> GetConnectionAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_connection.State == ConnectionState.Open)
      return _connection;

    await _semaphore.WaitAsync(ct);
    try
    {
      if (_connection.State == ConnectionState.Open)
        return _connection;

      await _connection.OpenAsync(ct);
      return _connection;
    }
    finally
    {
      _semaphore.Release();
    }
  }
}
