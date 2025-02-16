namespace Ghost.Core.Data;

public interface IDatabaseProvider : IStorageProvider
{
  string Name { get; }
  DatabaseType DatabaseType { get; }
  DatabaseCapabilities Capabilities { get; }
}
