using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ghost.Core.Data.Implementations
{
  /// <summary>
  /// PostgreSQL implementation of the database client.
  /// </summary>
  public class PostgreSqlClient : IDatabaseClient
  {
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<PostgreSqlClient> _logger;
    private bool _disposed;

    /// <summary>
    /// Gets the connection string being used by this client.
    /// </summary>
    public string ConnectionString => _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlClient"/> class.
    /// </summary>
    /// <param name="connectionString">The connection string to the PostgreSQL database.</param>
    /// <param name="logger">The logger.</param>
    public PostgreSqlClient(string connectionString, ILogger<PostgreSqlClient> logger)
    {
      _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates and opens a new connection to the database.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An open connection to the database.</returns>
    private async Task<NpgsqlConnection> CreateAndOpenConnectionAsync(CancellationToken ct = default)
    {
      var connection = new NpgsqlConnection(_connectionString);
      await connection.OpenAsync(ct);
      return connection;
    }

    /// <inheritdoc />
    public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      await using var connection = await CreateAndOpenConnectionAsync(ct);
      return await connection.QuerySingleOrDefaultAsync<T>(
          new CommandDefinition(sql, param, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      await using var connection = await CreateAndOpenConnectionAsync(ct);
      return await connection.QueryAsync<T>(
          new CommandDefinition(sql, param, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      await using var connection = await CreateAndOpenConnectionAsync(ct);
      return await connection.ExecuteAsync(
          new CommandDefinition(sql, param, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
      ThrowIfDisposed();

      var connection = await CreateAndOpenConnectionAsync(ct);
      var transaction = await connection.BeginTransactionAsync(ct);

      return new PostgreSqlTransaction(connection, transaction);
    }

    /// <inheritdoc />
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
    {
      ThrowIfDisposed();

      // PostgreSQL-specific query to check if a table exists
      const string sql = @"
                SELECT EXISTS (
                    SELECT FROM information_schema.tables 
                    WHERE table_schema = 'public' 
                    AND table_name = @TableName
                );";

      await using var connection = await CreateAndOpenConnectionAsync(ct);
      return await connection.QuerySingleOrDefaultAsync<bool>(
          new CommandDefinition(sql, new
          {
              TableName = tableName
          }, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default)
    {
      ThrowIfDisposed();

      // PostgreSQL-specific query to get all table names
      const string sql = @"
                SELECT table_name 
                FROM information_schema.tables 
                WHERE table_schema = 'public'
                ORDER BY table_name;";

      await using var connection = await CreateAndOpenConnectionAsync(ct);
      return await connection.QueryAsync<string>(
          new CommandDefinition(sql, cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
      try
      {
        await using var connection = await CreateAndOpenConnectionAsync(ct);
        // Simple query to test connection
        await connection.ExecuteScalarAsync<int>("SELECT 1", ct);
        return true;
      }
      catch (Exception ex)
      {
        _logger.LogError(ex, "Failed to connect to PostgreSQL database");
        return false;
      }
    }

    /// <summary>
    /// Throws an ObjectDisposedException if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
      if (_disposed)
        throw new ObjectDisposedException(nameof(PostgreSqlClient));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
      if (_disposed) return;

      await _lock.WaitAsync();
      try
      {
        if (_disposed) return;
        _disposed = true;

        _lock.Dispose();
      }
      finally
      {
        _lock.Release();
      }
    }

    /// <summary>
    /// Represents a PostgreSQL transaction.
    /// </summary>
    private class PostgreSqlTransaction : IGhostTransaction
    {
      private readonly NpgsqlConnection _connection;
      private readonly NpgsqlTransaction _transaction;
      private bool _disposed;
      private bool _committed;

      public PostgreSqlTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
      {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
      }

      /// <inheritdoc />
      public async Task CommitAsync(CancellationToken ct = default)
      {
        ThrowIfDisposed();
        await _transaction.CommitAsync(ct);
        _committed = true;
      }

      /// <inheritdoc />
      public async Task RollbackAsync(CancellationToken ct = default)
      {
        ThrowIfDisposed();

        if (!_committed)
        {
          await _transaction.RollbackAsync(ct);
        }
      }

      /// <inheritdoc />
      public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
      {
        ThrowIfDisposed();
        return await _connection.QuerySingleOrDefaultAsync<T>(
            new CommandDefinition(sql, param, _transaction, cancellationToken: ct));
      }

      /// <inheritdoc />
      public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
      {
        ThrowIfDisposed();
        return await _connection.QueryAsync<T>(
            new CommandDefinition(sql, param, _transaction, cancellationToken: ct));
      }

      /// <inheritdoc />
      public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
      {
        ThrowIfDisposed();
        return await _connection.ExecuteAsync(
            new CommandDefinition(sql, param, _transaction, cancellationToken: ct));
      }

      /// <inheritdoc />
      public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default)
      {
        ThrowIfDisposed();

        var totalAffected = 0;

        foreach (var (sql, param) in commands)
        {
          var affected = await _connection.ExecuteAsync(
              new CommandDefinition(sql, param, _transaction, cancellationToken: ct));
          totalAffected += affected;
        }

        return totalAffected;
      }

      /// <summary>
      /// Throws an ObjectDisposedException if this object has been disposed.
      /// </summary>
      private void ThrowIfDisposed()
      {
        if (_disposed)
          throw new ObjectDisposedException(nameof(PostgreSqlTransaction));
      }

      /// <inheritdoc />
      public async ValueTask DisposeAsync()
      {
        if (_disposed) return;

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
    }
  }
}
