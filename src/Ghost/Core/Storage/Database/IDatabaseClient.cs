namespace Ghost.Core.Storage.Database;

/// <summary>
/// Interface for database operations
/// </summary>
public interface IDatabaseClient : IAsyncDisposable
{
    Task<T> QuerySingleAsync<T>(string sql, object param = null);
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null);
    Task<int> ExecuteAsync(string sql, object param = null);
    Task<IGhostTransaction> BeginTransactionAsync();
}