using Ghost.Core.Data;
using Microsoft.Extensions.Logging;
namespace Ghost.Core.Testing.InMemory;

/// <summary>
/// In-memory implementation of IDatabaseClient for testing purposes.
/// </summary>
internal class InMemoryDatabaseClient : IDatabaseClient
{
  private readonly InMemoryGhostData _data;
  private readonly ILogger _logger;
  private bool _disposed;

  /// <summary>
  /// Initializes a new instance of the <see cref="InMemoryDatabaseClient"/> class.
  /// </summary>
  /// <param name="data">The in-memory data store.</param>
  /// <param name="logger">The logger.</param>
  public InMemoryDatabaseClient(InMemoryGhostData data, ILogger logger)
  {
    _data = data ?? throw new ArgumentNullException(nameof(data));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <inheritdoc />
  public DatabaseType DatabaseType => DatabaseType.InMemory;

  /// <inheritdoc />
  public Task<bool> IsAvailableAsync(CancellationToken ct = default)
  {
    ThrowIfDisposed();

    return Task.FromResult(true);
  }

  public string ConnectionString
  {
    get;
  }

  /// <inheritdoc />
  public Task<long> GetStorageSizeAsync(CancellationToken ct = default)
  {
    ThrowIfDisposed();

    return Task.FromResult(0L);
  }

  /// <inheritdoc />
  public Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    return _data.QuerySingleAsync<T>(sql, param, ct);
  }

  /// <inheritdoc />
  public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    return _data.QueryAsync<T>(sql, param, ct);
  }

  /// <inheritdoc />
  public Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    return _data.ExecuteAsync(sql, param, ct);
  }

  /// <inheritdoc />
  public Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default)
  {
    ThrowIfDisposed();

    return _data.BeginTransactionAsync(ct);
  }

  /// <inheritdoc />
  public Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default)
  {
    ThrowIfDisposed();

    return _data.TableExistsAsync(tableName, ct);
  }

  /// <inheritdoc />
  public Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default)
  {
    ThrowIfDisposed();

    return _data.GetTableNamesAsync(ct);
  }

  /// <summary>
  /// Throws if this object has been disposed.
  /// </summary>
  private void ThrowIfDisposed()
  {
    if (_disposed)
      throw new ObjectDisposedException(nameof(InMemoryDatabaseClient));
  }

  /// <inheritdoc />
  public ValueTask DisposeAsync()
  {
    if (_disposed)
      return ValueTask.CompletedTask;

    _disposed = true;

    return ValueTask.CompletedTask;
  }
}
