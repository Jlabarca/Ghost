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

/// <summary>
/// Represents a cache entry with metadata
/// </summary>
public class CacheEntry<T>
{
    public T Value { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
}
