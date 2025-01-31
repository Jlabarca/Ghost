namespace Ghost.Infrastructure.Storage;

internal class CacheEntry
{
  public string Value { get; set; }
  public DateTime? ExpiresAt { get; set; }
  public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
}
