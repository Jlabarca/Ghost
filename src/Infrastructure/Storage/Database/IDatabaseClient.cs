namespace Ghost.Infrastructure.Storage.Database;

public interface IDatabaseClient : IAsyncDisposable
{
  Task<T> QuerySingleAsync<T>(string sql, object param = null);
  Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null);
  Task<int> ExecuteAsync(string sql, object param = null);
  Task<IGhostTransaction> BeginTransactionAsync();
}
