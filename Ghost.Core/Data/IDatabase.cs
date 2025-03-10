// namespace Ghost.Core.Data;
//
// public interface IDatabase : IStorageProvider
// {
//   DatabaseType DatabaseType { get; }
//
//   // Key-value operations
//   Task<T> GetValueAsync<T>(string key, CancellationToken ct = default(CancellationToken));
//   Task<bool> SetValueAsync<T>(string key, T value, CancellationToken ct = default(CancellationToken));
//   Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken));
//   Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken));
//
//   // SQL operations
//   Task<T> QuerySingleAsync<T>(string sql, object param = null, CancellationToken ct = default(CancellationToken));
//   Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null, CancellationToken ct = default(CancellationToken));
//   Task<int> ExecuteAsync(string sql, object param = null, CancellationToken ct = default(CancellationToken));
//   Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default(CancellationToken));
//
//   // Schema operations
//   Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken));
//   Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken));
// }
