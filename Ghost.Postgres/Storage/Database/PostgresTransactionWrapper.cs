using Ghost.Core.Exceptions;
using System.Data;
namespace Ghost.Core.Storage.Database;

/// <summary>
/// Transaction wrapper for PostgreSQL
/// </summary>
public class PostgresTransactionWrapper : IGhostTransaction
{
  private readonly IDbTransaction _transaction;
  private bool _disposed;

  public PostgresTransactionWrapper(IDbTransaction transaction)
  {
    _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
  }

  public Task CommitAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresTransactionWrapper));

    try
    {
      _transaction.Commit();
      return Task.CompletedTask;
    }
    catch (Exception ex)
    {
      L.LogError("Failed to commit PostgreSQL transaction", ex);
      throw new GhostException(
          "Failed to commit PostgreSQL transaction",
          ex,
          ErrorCode.StorageOperationFailed);
    }
  }

  public Task RollbackAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresTransactionWrapper));

    try
    {
      _transaction.Rollback();
      return Task.CompletedTask;
    }
    catch (Exception ex)
    {
      L.LogError("Failed to rollback PostgreSQL transaction", ex);
      throw new GhostException(
          "Failed to rollback PostgreSQL transaction",
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
      L.LogError("Error disposing PostgreSQL transaction", ex);
      throw;
    }
  }
}
