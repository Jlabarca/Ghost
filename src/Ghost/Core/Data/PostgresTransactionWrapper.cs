using Npgsql;
namespace Ghost.Core.Data;

public class PostgresTransactionWrapper : IGhostTransaction
{
  private readonly NpgsqlTransaction _transaction;
  private bool _disposed;

  public PostgresTransactionWrapper(NpgsqlTransaction transaction)
  {
    _transaction = transaction;
  }

  public async Task CommitAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresTransactionWrapper));
    await _transaction.CommitAsync();
  }

  public async Task RollbackAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(PostgresTransactionWrapper));
    await _transaction.RollbackAsync();
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed) return;
    _disposed = true;
    await _transaction.DisposeAsync();
  }
}
