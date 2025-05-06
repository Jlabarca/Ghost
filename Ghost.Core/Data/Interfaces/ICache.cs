namespace Ghost.Core.Data;

/// <summary>
/// Interface for cache providers, supporting both memory and distributed caches.
/// </summary>
public interface ICache : IAsyncDisposable
{
  /// <summary>
  /// Gets the value associated with the specified key.
  /// </summary>
  /// <typeparam name="T">The type of the value to get.</typeparam>
  /// <param name="key">The key of the value to get.</param>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>The value associated with the specified key, or default(T) if the key does not exist.</returns>
  Task<T?> GetAsync<T>(string key, CancellationToken ct = default);

  /// <summary>
  /// Sets the value associated with the specified key.
  /// </summary>
  /// <typeparam name="T">The type of the value to set.</typeparam>
  /// <param name="key">The key of the value to set.</param>
  /// <param name="value">The value to set.</param>
  /// <param name="expiry">Optional expiration time.</param>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>True if the value was set, otherwise false.</returns>
  Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);

  /// <summary>
  /// Removes the value associated with the specified key.
  /// </summary>
  /// <param name="key">The key of the value to remove.</param>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>True if the key was found and removed, otherwise false.</returns>
  Task<bool> DeleteAsync(string key, CancellationToken ct = default);

  /// <summary>
  /// Checks if the specified key exists in the cache.
  /// </summary>
  /// <param name="key">The key to check.</param>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>True if the key exists, otherwise false.</returns>
  Task<bool> ExistsAsync(string key, CancellationToken ct = default);

  /// <summary>
  /// Gets multiple values associated with the specified keys.
  /// </summary>
  /// <typeparam name="T">The type of the values to get.</typeparam>
  /// <param name="keys">The keys of the values to get.</param>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>A dictionary mapping keys to values. If a key does not exist, it will not be included in the dictionary.</returns>
  Task<IDictionary<string, T?>> GetManyAsync<T>(IEnumerable<string> keys, CancellationToken ct = default);

  /// <summary>
  /// Sets multiple values associated with the specified keys.
  /// </summary>
  /// <typeparam name="T">The type of the values to set.</typeparam>
  /// <param name="items">A dictionary mapping keys to values.</param>
  /// <param name="expiry">Optional expiration time.</param>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>True if all values were set, otherwise false.</returns>
  Task<bool> SetManyAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default);

  /// <summary>
  /// Removes multiple values associated with the specified keys.
  /// </summary>
  /// <param name="keys">The keys of the values to remove.</param>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>The number of keys that were found and removed.</returns>
  Task<int> DeleteManyAsync(IEnumerable<string> keys, CancellationToken ct = default);

  /// <summary>
  /// Updates the expiration time for a key.
  /// </summary>
  /// <param name="key">The key to update.</param>
  /// <param name="expiry">The new expiration time.</param>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>True if the key was found and the expiration time was updated, otherwise false.</returns>
  Task<bool> UpdateExpiryAsync(string key, TimeSpan expiry, CancellationToken ct = default);

  /// <summary>
  /// Clears all items from the cache.
  /// </summary>
  /// <param name="ct">Optional cancellation token.</param>
  /// <returns>True if the cache was cleared, otherwise false.</returns>
  Task<bool> ClearAsync(CancellationToken ct = default);

  /// <summary>
  /// Gets the name of the cache provider.
  /// </summary>
  string Name { get; }

  /// <summary>
  /// Gets whether this cache is local or distributed.
  /// </summary>
  bool IsDistributed { get; }
}