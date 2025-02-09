using System.Data;
namespace Ghost.Core.Storage.Database;

/// <summary>
/// Transaction wrapper for SQLite
/// </summary>
public class SQLiteTransactionWrapper : IGhostTransaction
{
  private readonly IDbTransaction _transaction;
  private bool _disposed;

  public SQLiteTransactionWrapper(IDbTransaction transaction)
  {
    _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
  }

  public Task CommitAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteTransactionWrapper));

    try
    {
      _transaction.Commit();
      return Task.CompletedTask;
    }
    catch (Exception ex)
    {
      G.LogError("Failed to commit SQLite transaction", ex);
      throw new GhostException(
          "Failed to commit SQLite transaction",
          ex,
          ErrorCode.StorageOperationFailed);
    }
  }

  public Task RollbackAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(SQLiteTransactionWrapper));

    try
    {
      _transaction.Rollback();
      return Task.CompletedTask;
    }
    catch (Exception ex)
    {
      G.LogError("Failed to rollback SQLite transaction", ex);
      throw new GhostException(
          "Failed to rollback SQLite transaction",
          ex,
          ErrorCode.StorageOperationFailed);
    }
  }

  public ValueTask DisposeAsync()
  {
    if (_disposed) return ValueTask.CompletedTask;
    _disposed = true;

    try
    {
      _transaction.Dispose();
      return ValueTask.CompletedTask;
    }
    catch (Exception ex)
    {
      G.LogError("Error disposing SQLite transaction", ex);
      throw;
    }
  }
}
