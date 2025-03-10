namespace Ghost.Core.Data;

public interface IGhostTransaction : IAsyncDisposable
{
  Task CommitAsync();
  Task RollbackAsync();
}
