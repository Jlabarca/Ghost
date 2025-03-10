namespace Ghost.Core.Data;

public interface ISchemaManager
{
  // Single type initialization
  Task InitializeAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class;

  // Multiple types initialization
  Task InitializeAsync(Type[] types, CancellationToken ct = default(CancellationToken));

  // Schema existence check
  Task<bool> ExistsAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class;

  // Schema validation
  Task<bool> ValidateAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class;

  // Schema migration
  Task MigrateAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class;

  // Schema reset
  Task ResetAsync(CancellationToken ct = default(CancellationToken));

  // Schema info
  Task<IEnumerable<string>> GetTablesAsync(CancellationToken ct = default(CancellationToken));
  Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default(CancellationToken));
  Task<IEnumerable<IndexInfo>> GetIndexesAsync(string tableName, CancellationToken ct = default(CancellationToken));
}
