using Microsoft.Data.Sqlite;
namespace Ghost.Infrastructure.Storage.Database;

public class SQLiteTransactionWrapper : IGhostTransaction
{
  private readonly SqliteTransaction _transaction;
  private bool _disposed;

  public SQLiteTransactionWrapper(SqliteTransaction transaction)
  {
    _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
  }

  public async Task CommitAsync(CancellationToken cancellationToken = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteTransactionWrapper));
    await _transaction.CommitAsync(cancellationToken);
  }

  public async Task RollbackAsync(CancellationToken cancellationToken = default)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteTransactionWrapper));
    await _transaction.RollbackAsync(cancellationToken);
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
