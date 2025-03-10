namespace Ghost.Core.Data
{
  public interface ICacheProvider : IStorageProvider
  {
    string Name { get; }
    CacheCapabilities Capabilities { get; }
  }
}
