namespace Ghost.Core.Storage;

/// <summary>
/// Represents a cache entry with metadata
/// </summary>
public class CacheEntry<T>
{
  public T Value { get; set; }
  public DateTime? ExpiresAt { get; set; }
  public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
}
