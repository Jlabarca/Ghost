namespace Ghost.Infrastructure.Storage;

public interface IRedisClient : IAsyncDisposable
{
  Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null);
  Task<T?> GetAsync<T>(string key);
  Task<bool> DeleteAsync(string key);
  Task<bool> ExistsAsync(string key);
  Task<long> PublishAsync(string channel, string message);
  IAsyncEnumerable<string> SubscribeAsync(string channel, CancellationToken cancellationToken = default);
}
