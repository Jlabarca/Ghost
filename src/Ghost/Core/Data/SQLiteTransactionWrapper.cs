using Microsoft.Data.Sqlite;
namespace Ghost.Core.Data;
public class SQLiteTransactionWrapper : IGhostTransaction
{
  private readonly SqliteTransaction _transaction;
  private bool _disposed;

  public SQLiteTransactionWrapper(SqliteTransaction transaction)
  {
    _transaction = transaction;
  }

  public async Task CommitAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteTransactionWrapper));
    await _transaction.CommitAsync();
  }

  public async Task RollbackAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteTransactionWrapper));
    await _transaction.RollbackAsync();
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed) return;

    _disposed = true;
    await _transaction.DisposeAsync();

  }
}
