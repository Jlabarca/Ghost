namespace Ghost.Infrastructure.Storage.Database;

public interface IGhostTransaction : IAsyncDisposable
{
  Task CommitAsync(CancellationToken cancellationToken = default);
  Task RollbackAsync(CancellationToken cancellationToken = default);
}
