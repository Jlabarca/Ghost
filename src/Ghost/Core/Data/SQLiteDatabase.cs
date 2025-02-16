using Dapper;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Text.Json;

namespace Ghost.Core.Data;

public class SQLiteDatabase : IDatabaseClient
{
  private readonly SqliteConnection _connection;
  private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
  private bool _disposed;

  public SQLiteDatabase(string dbPath)
  {
    SqliteConnectionStringBuilder? builder = new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Cache = SqliteCacheMode.Private,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = 30,
        ForeignKeys = true
    };

    _connection = new SqliteConnection(builder.ToString());
  }

  public DatabaseType DatabaseType
  {
    get
    {
      return DatabaseType.SQLite;
    }
  }

  public async Task<bool> IsAvailableAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    try
    {
      SqliteConnection conn = await GetConnectionAsync(ct);
      return conn.State == ConnectionState.Open;
    }
    catch
    {
      return false;
    }
  }

  public async Task<long> GetStorageSizeAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
    string sql = "SELECT page_count * page_size as size FROM pragma_page_count(), pragma_page_size()";
    return await conn.ExecuteScalarAsync<long>(sql);
  }

  public async Task<T> GetValueAsync<T>(string key, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
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
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            INSERT OR REPLACE INTO key_value_store (key, value)
            VALUES (@key, @value)";

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
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
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
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
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
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
    return await conn.QuerySingleAsync<T>(sql, param);
  }

  public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
    return await conn.QueryAsync<T>(sql, param);
  }

  public async Task<int> ExecuteAsync(string sql, object param = null, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
    return await conn.ExecuteAsync(sql, param);
  }

  public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
    SqliteTransaction transaction = await conn.BeginTransactionAsync(ct) as SqliteTransaction;
    return new SQLiteTransactionWrapper(transaction);
  }

  public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            SELECT 1 
            FROM sqlite_master 
            WHERE type='table' 
            AND name = @tableName";

    int? result = await conn.QuerySingleOrDefaultAsync<int?>(sql, new
    {
        tableName
    });
    return result.HasValue;
  }

  public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteDatabase));

    SqliteConnection conn = await GetConnectionAsync(ct);
    string sql = @"
            SELECT name 
            FROM sqlite_master 
            WHERE type='table'";

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
  public Task<bool> DeleteKeyAsync(string key, CancellationToken ct = default(CancellationToken))
  {
    return DeleteAsync(key, ct);
  }
  public Task<bool> KeyExistsAsync(string key, CancellationToken ct = default(CancellationToken))
  {
    return ExistsAsync(key, ct);
  }

  private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_connection.State == ConnectionState.Open)
      return _connection;

    await _semaphore.WaitAsync(ct);
    try
    {
      if (_connection.State == ConnectionState.Open)
        return _connection;

      await _connection.OpenAsync(ct);
      await ConfigureConnectionAsync(_connection);
      return _connection;
    }
    finally
    {
      _semaphore.Release();
    }
  }

  private static async Task ConfigureConnectionAsync(SqliteConnection connection)
  {
    await using var command = connection.CreateCommand();

    command.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -2000;
            PRAGMA foreign_keys = ON;";

    await command.ExecuteNonQueryAsync();
  }
}