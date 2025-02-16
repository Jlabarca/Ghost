// namespace Ghost.Core.Storage.Cache;
//
// /// <summary>
// /// Cache interface supporting both local and distributed caching
// /// </summary>
// public interface ICache : IStorageProvider
// {
//   Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
//   Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
//   Task<bool> DeleteAsync(string key, CancellationToken ct = default);
//   Task<bool> ExistsAsync(string key, CancellationToken ct = default);
//   Task<long> IncrementAsync(string key, long value = 1, CancellationToken ct = default);
//   Task<bool> ExpireAsync(string key, TimeSpan expiry, CancellationToken ct = default);
//   Task ClearAsync(CancellationToken ct = default);
// }
