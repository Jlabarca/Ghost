namespace Ghost.Core.Data;

public interface IStorageProvider : IAsyncDisposable
{
  Task<bool> IsAvailableAsync(CancellationToken ct = default(CancellationToken));
  Task<long> GetStorageSizeAsync(CancellationToken ct = default(CancellationToken));
}
