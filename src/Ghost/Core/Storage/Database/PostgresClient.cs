// using Dapper;
// using Npgsql;
// using System.Data;
//
// namespace Ghost.Core.Data;
// /// <summary>
// /// PostgreSQL implementation of database client
// /// </summary>
// public class PostgresClient : IDatabaseClient
// {
//   private readonly string _connectionString;
//   private NpgsqlConnection _connection;
//   private readonly SemaphoreSlim _semaphore = new(1, 1);
//   private bool _disposed;
//
//   public PostgresClient(string connectionString)
//   {
//     if (string.IsNullOrEmpty(connectionString))
//       throw new ArgumentException("Connection string cannot be empty", nameof(connectionString));
//
//     _connectionString = connectionString;
//   }
//
//   internal async Task<NpgsqlConnection> GetConnectionAsync()
//   {
//     if (_connection?.State == ConnectionState.Open)
//       return _connection;
//
//     await _semaphore.WaitAsync();
//     try
//     {
//       if (_connection?.State == ConnectionState.Open)
//         return _connection;
//
//       _connection = new NpgsqlConnection(_connectionString);
//       await _connection.OpenAsync();
//
//       G.LogDebug("Opened PostgreSQL connection");
//       return _connection;
//     }
//     catch (Exception ex)
//     {
//       G.LogError("Failed to open PostgreSQL connection", ex);
//       throw new GhostException(
//           "Failed to open PostgreSQL connection",
//           ex,
//           ErrorCode.StorageConnectionFailed);
//     }
//     finally
//     {
//       _semaphore.Release();
//     }
//   }
//
//   public async Task<T> QuerySingleAsync<T>(string sql, object param = null)
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));
//
//     try
//     {
//       var conn = await GetConnectionAsync();
//       return await conn.QuerySingleAsync<T>(sql, param);
//     }
//     catch (Exception ex)
//     {
//       G.LogError($"PostgreSQL query failed: {sql}", ex);
//       throw new GhostException(
//           $"PostgreSQL query failed: {sql}",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//   }
//
//   public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));
//
//     try
//     {
//       var conn = await GetConnectionAsync();
//       return await conn.QueryAsync<T>(sql, param);
//     }
//     catch (Exception ex)
//     {
//       G.LogError($"PostgreSQL query failed: {sql}", ex);
//       throw new GhostException(
//           $"PostgreSQL query failed: {sql}",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//   }
//
//   public async Task<int> ExecuteAsync(string sql, object param = null)
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));
//
//     try
//     {
//       var conn = await GetConnectionAsync();
//       return await conn.ExecuteAsync(sql, param);
//     }
//     catch (Exception ex)
//     {
//       G.LogError($"PostgreSQL execute failed: {sql}", ex);
//       throw new GhostException(
//           $"PostgreSQL execute failed: {sql}",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//   }
//
//   public async Task<IGhostTransaction> BeginTransactionAsync()
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(PostgresClient));
//
//     try
//     {
//       var conn = await GetConnectionAsync();
//       var transaction = await conn.BeginTransactionAsync();
//       return new PostgresTransactionWrapper(transaction);
//     }
//     catch (Exception ex)
//     {
//       G.LogError("Failed to begin PostgreSQL transaction", ex);
//       throw new GhostException(
//           "Failed to begin PostgreSQL transaction",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//   }
//
//   public async ValueTask DisposeAsync()
//   {
//     if (_disposed) return;
//
//     await _semaphore.WaitAsync();
//     try
//     {
//       if (_disposed) return;
//       _disposed = true;
//
//       if (_connection != null)
//       {
//         await _connection.DisposeAsync();
//         G.LogDebug("Disposed PostgreSQL connection");
//       }
//     }
//     finally
//     {
//       _semaphore.Release();
//       _semaphore.Dispose();
//     }
//   }
// }
