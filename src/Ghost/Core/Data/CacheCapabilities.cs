namespace Ghost.Core.Data
{
  public class CacheCapabilities
  {
    public bool SupportsDistributedLock { get; init; }
    public bool SupportsAtomicOperations { get; init; }
    public bool SupportsPubSub { get; init; }
    public bool SupportsTagging { get; init; }
    public long MaxKeySize { get; init; }
    public long MaxValueSize { get; init; }
  }
}
