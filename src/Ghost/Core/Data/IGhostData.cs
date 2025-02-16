using Ghost.Core.Storage.Database;
namespace Ghost.Core.Data;

public interface IGhostData : IAsyncDisposable
{
  Task InitializeAsync();
  DatabaseType DatabaseType { get; }
  ISchemaManager Schema { get; }
  Task<T?> GetValueAsync<T>(string key, CancellationToken ct = default(CancellationToken));
  Task SetValueAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken));
  Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken));
  Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken));

  Task<T> QuerySingleAsync<T>(string sql, object param = null, CancellationToken ct = default(CancellationToken));
  Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null, CancellationToken ct = default(CancellationToken));
  Task<int> ExecuteAsync(string sql, object param = null, CancellationToken ct = default(CancellationToken));
  Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default(CancellationToken));

  Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken));
  Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken));
  IDatabaseClient GetDatabaseClient();
}
