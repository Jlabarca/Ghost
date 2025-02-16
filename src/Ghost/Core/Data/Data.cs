namespace Ghost.Core.Data;

// SQL operations
public interface IDatabaseClient : IStorageProvider
{
  // SQL operations
  Task<T> QuerySingleAsync<T>(string sql, object param = null, CancellationToken ct = default);
  Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null, CancellationToken ct = default);
  Task<int> ExecuteAsync(string sql, object param = null, CancellationToken ct = default);
  Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default);

  // Add these schema-related operations that were in IDatabase
  Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default);
  Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default);

  // Add database type info
  DatabaseType DatabaseType { get; }
}


// Schema operations
public interface ISchemaProvider : IStorageProvider
{
  Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default);
  Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default);
}