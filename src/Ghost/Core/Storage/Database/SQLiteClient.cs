using Dapper;
using Microsoft.Data.Sqlite;
using System.Data;
namespace Ghost.Core.Storage.Database;

/// <summary>
/// SQLite implementation of database client
/// </summary>
public class SQLiteClient : IDatabaseClient
{
  private readonly string _connectionString;
  private SqliteConnection _connection;
  private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
  private bool _disposed;

  public SQLiteClient(string dbPath)
  {
    if (string.IsNullOrEmpty(dbPath))
      throw new ArgumentException("Database path cannot be empty", nameof(dbPath));

    // Foundation layer: Basic connection configuration
    var builder = new SqliteConnectionStringBuilder
    {
        DataSource = dbPath,
        Cache = SqliteCacheMode.Private,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
        DefaultTimeout = 5,
        ForeignKeys = true,
    };

    _connectionString = builder.ToString();
  }

  private async Task ConfigureConnectionAsync(SqliteConnection connection)
  {
    // Configuration layer: Setting up WAL mode and other PRAGMAs
    using var command = connection.CreateCommand();
    command.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA cache_size = -2000;"; // 2MB cache size
    await command.ExecuteNonQueryAsync();

    // Verify WAL mode is active (important for system reliability)
    command.CommandText = "PRAGMA journal_mode";
    var journalMode = await command.ExecuteScalarAsync();
    if (journalMode?.ToString()?.ToUpperInvariant() != "WAL")
    {
      G.LogError($"Failed to enable WAL mode. Current mode: {journalMode}");
      throw new GhostException(
          "Failed to enable WAL mode",
          ErrorCode.StorageConfigurationFailed);
    }
  }

  private async Task<SqliteConnection> GetConnectionAsync()
  {
    if (_connection?.State == ConnectionState.Open)
      return _connection;

    await _semaphore.WaitAsync();
    try
    {
      if (_connection?.State == ConnectionState.Open)
        return _connection;

      _connection = new SqliteConnection(_connectionString);
      await _connection.OpenAsync();
      await ConfigureConnectionAsync(_connection); // Configure after opening

      G.LogDebug("Opened and configured SQLite connection with WAL mode");
      return _connection;
    }
    catch (Exception ex)
    {
      G.LogError("Failed to open/configure SQLite connection", ex);
      throw new GhostException(
          "Failed to open/configure SQLite connection",
          ex,
          ErrorCode.StorageConnectionFailed);
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
      var conn = await GetConnectionAsync();
      return await conn.QuerySingleAsync<T>(sql, param);
    }
    catch (Exception ex)
    {
      G.LogError($"SQLite query failed: {sql}", ex);
      throw new GhostException(
          $"SQLite query failed: {sql}",
          ex,
          ErrorCode.StorageOperationFailed);
    }
  }

  public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteClient));

    try
    {
      var conn = await GetConnectionAsync();
      return await conn.QueryAsync<T>(sql, param);
    }
    catch (Exception ex)
    {
      G.LogError($"SQLite query failed: {sql}", ex);
      throw new GhostException(
          $"SQLite query failed: {sql}",
          ex,
          ErrorCode.StorageOperationFailed);
    }
  }

  public async Task<int> ExecuteAsync(string sql, object param = null)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteClient));

    try
    {
      var conn = await GetConnectionAsync();
      return await conn.ExecuteAsync(sql, param);
    }
    catch (Exception ex)
    {
      G.LogError($"SQLite execute failed: {sql}", ex);
      throw new GhostException(
          $"SQLite execute failed: {sql}",
          ex,
          ErrorCode.StorageOperationFailed);
    }
  }

  public async Task<IGhostTransaction> BeginTransactionAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteClient));

    try
    {
      var conn = await GetConnectionAsync();
      var transaction = await conn.BeginTransactionAsync();
      return new SQLiteTransactionWrapper(transaction);
    }
    catch (Exception ex)
    {
      G.LogError("Failed to begin SQLite transaction", ex);
      throw new GhostException(
          "Failed to begin SQLite transaction",
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
      if (_disposed) return;
      _disposed = true;

      if (_connection != null)
      {
        await _connection.DisposeAsync();
        G.LogDebug("Disposed SQLite connection");
      }
    }
    finally
    {
      _semaphore.Release();
      _semaphore.Dispose();
    }
  }
}
