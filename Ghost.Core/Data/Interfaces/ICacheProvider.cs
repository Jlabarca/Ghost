// namespace Ghost.Core.Data;
//
// /// <summary>
// /// Interface for cache providers
// /// </summary>
// public interface ICacheProvider : RT
// {
//     Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
//     Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
//     Task<bool> DeleteAsync(string key, CancellationToken ct = default);
//     Task<bool> ExistsAsync(string key, CancellationToken ct = default);
//     Task<bool> ExpireAsync(string key, TimeSpan expiry, CancellationToken ct = default);
//     Task ClearAsync(CancellationToken ct = default);
//
//     string Name { get; }
//     CacheCapabilities Capabilities { get; }
// }
