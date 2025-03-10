namespace Ghost.Core.Storage.Database;

/// <summary>
/// Interface for database transactions
/// </summary>
public interface IGhostTransaction : IAsyncDisposable
{
  Task CommitAsync();
  Task RollbackAsync();
}
