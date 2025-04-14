namespace Ghost.Core.Data;

public interface ICache : IStorageProvider
{
  Task<T> GetAsync<T>(string key, CancellationToken ct = default);
  Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
  Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken));
  Task<bool> ExpireAsync(string key, TimeSpan expiry, CancellationToken ct = default(CancellationToken));
  Task ClearAsync(CancellationToken ct = default(CancellationToken));
  Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken));
  Task<List<T>> GetAllAsync<T>(T channelsActive);
}
