namespace Ghost.Core.Data
{
  public class CacheEntry<T>
  {
    public required T Value { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public required string TypeName { get; set; }
    public bool IsExpired
    {
      get
      {
        return ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
      }
    }
  }
}
