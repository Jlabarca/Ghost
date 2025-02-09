// Ghost/Core/Storage/IStorageProvider.cs
namespace Ghost.Core.Storage;

/// <summary>
/// Base interface for all storage providers (Redis, SQLite, Postgres)
/// </summary>
public interface IStorageProvider : IAsyncDisposable
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<long> GetStorageSizeAsync(CancellationToken ct = default);
}