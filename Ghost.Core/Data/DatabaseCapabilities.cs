namespace Ghost.Core.Data;

public class DatabaseCapabilities
{
  public bool SupportsConcurrentTransactions { get; init; }
  public bool SupportsSchemaless { get; init; }
  public bool SupportsFullTextSearch { get; init; }
  public bool SupportsJson { get; init; }
  public int MaxConnections { get; init; }
  public long MaxDatabaseSize { get; init; }
}
