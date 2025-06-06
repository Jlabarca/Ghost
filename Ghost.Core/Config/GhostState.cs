// using Ghost.Config;
// using Ghost.Exceptions;
// using Microsoft.Extensions.Logging;
// using System.Collections.Concurrent;
// namespace Ghost.Data;
//
// /// <summary>
// /// Main implementation of state management with persistence and change tracking
// /// </summary>
// public class  GhostState: IGhostState
// {
//   private readonly IGhostData _data;
//   private readonly ILogger _logger;
//   private readonly ConcurrentDictionary<string, object> _cache;
//   private readonly SemaphoreSlim _lock = new(1, 1);
//   private bool _disposed;
//
//   public event EventHandler<StateChangedEventArgs> StateChanged;
//
//   public GhostState(IGhostData data, ILogger<GhostState> logger)
//   {
//     _data = data ?? throw new ArgumentNullException(nameof(data));
//     _logger = logger ?? throw new ArgumentNullException(nameof(logger));
//     _cache = new ConcurrentDictionary<string, object>();
//   }
//
//   public async Task<T> GetAsync<T>(string key)
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(GhostState));
//     if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));
//
//     // Check cache first
//     if (_cache.TryGetValue(key, out var cached) && cached is T typedValue)
//     {
//       return typedValue;
//     }
//
//     await _lock.WaitAsync();
//     try
//     {
//       // Double-check cache after acquiring lock
//       if (_cache.TryGetValue(key, out cached) && cached is T cachedValue)
//       {
//         return cachedValue;
//       }
//
//       // Get from storage
//       var result = await _data.QuerySingleAsync<T>(
//           "SELECT value FROM state WHERE key = @key ORDER BY timestamp DESC LIMIT 1",
//           new { key });
//
//       if (result != null)
//       {
//         _cache[key] = result;
//       }
//
//       return result;
//     }
//     catch (Exception ex)
//     {
//       _logger.LogError(ex, "Failed to get state for key: {Key}", key);
//       throw new GhostException(
//           $"Failed to get state for key: {key}",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//     finally
//     {
//       _lock.Release();
//     }
//   }
//
//   public async Task SetAsync<T>(string key, T value)
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(GhostState));
//     if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));
//
//     await _lock.WaitAsync();
//     try
//     {
//       // Get old value for change tracking
//       var oldValue = await GetAsync<T>(key);
//
//       // Store new value
//       await using var transaction = await _data.BeginTransactionAsync();
//       try
//       {
//         var timestamp = DateTime.UtcNow;
//
//         // Insert new state
//         await _data.ExecuteAsync(@"
//                     INSERT INTO state (key, value, change_type, timestamp)
//                     VALUES (@key, @value, @changeType, @timestamp)",
//             new
//             {
//                 key,
//                 value = System.Text.Json.JsonSerializer.Serialize(value),
//                 changeType = oldValue == null ?
//                     StateChangeType.Created :
//                     StateChangeType.Updated,
//                 timestamp
//             });
//
//         // Update cache
//         _cache[key] = value;
//
//         await transaction.CommitAsync();
//
//         // Notify change
//         var changeType = oldValue == null ?
//             StateChangeType.Created :
//             StateChangeType.Updated;
//         OnStateChanged(key, oldValue, value, changeType);
//       }
//       catch
//       {
//         await transaction.RollbackAsync();
//         throw;
//       }
//     }
//     catch (Exception ex)
//     {
//       _logger.LogError(ex, "Failed to set state for key: {Key}", key);
//       throw new GhostException(
//           $"Failed to set state for key: {key}",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//     finally
//     {
//       _lock.Release();
//     }
//   }
//
//   public async Task DeleteAsync(string key)
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(GhostState));
//     if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));
//
//     await _lock.WaitAsync();
//     try
//     {
//       // Get old value for change tracking
//       var oldValue = _cache.TryGetValue(key, out var cached) ? cached : null;
//
//       // Remove from storage
//       await using var transaction = await _data.BeginTransactionAsync();
//       try
//       {
//         await _data.ExecuteAsync(@"
//                     INSERT INTO state (key, value, change_type, timestamp)
//                     VALUES (@key, NULL, @changeType, @timestamp)",
//             new
//             {
//                 key,
//                 changeType = StateChangeType.Deleted,
//                 timestamp = DateTime.UtcNow
//             });
//
//         // Remove from cache
//         _cache.TryRemove(key, out _);
//
//         await transaction.CommitAsync();
//
//         // Notify change
//         OnStateChanged(key, oldValue, null, StateChangeType.Deleted);
//       }
//       catch
//       {
//         await transaction.RollbackAsync();
//         throw;
//       }
//     }
//     catch (Exception ex)
//     {
//       _logger.LogError(ex, "Failed to delete state for key: {Key}", key);
//       throw new GhostException(
//           $"Failed to delete state for key: {key}",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//     finally
//     {
//       _lock.Release();
//     }
//   }
//
//   public async Task<bool> ExistsAsync(string key)
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(GhostState));
//     if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));
//
//     // Check cache first
//     if (_cache.ContainsKey(key)) return true;
//
//     try
//     {
//       var count = await _data.QuerySingleAsync<int>(
//           "SELECT COUNT(*) FROM state WHERE key = @key",
//           new { key });
//       return count > 0;
//     }
//     catch (Exception ex)
//     {
//       _logger.LogError(ex, "Failed to check state existence for key: {Key}", key);
//       throw new GhostException(
//           $"Failed to check state existence for key: {key}",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//   }
//
//   public async Task<IEnumerable<StateEntry>> GetHistoryAsync(string key, int limit = 10)
//   {
//     if (_disposed) throw new ObjectDisposedException(nameof(GhostState));
//     if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key cannot be empty", nameof(key));
//     if (limit <= 0) throw new ArgumentException("Limit must be positive", nameof(limit));
//
//     try
//     {
//       var entries = await _data.QueryAsync<StateEntry>(@"
//                 SELECT
//                     key,
//                     value,
//                     change_type as ChangeType,
//                     timestamp as Timestamp
//                 FROM state
//                 WHERE key = @key
//                 ORDER BY timestamp DESC
//                 LIMIT @limit",
//           new { key, limit });
//
//       return entries ?? Enumerable.Empty<StateEntry>();
//     }
//     catch (Exception ex)
//     {
//       _logger.LogError(ex, "Failed to get state history for key: {Key}", key);
//       throw new GhostException(
//           $"Failed to get state history for key: {key}",
//           ex,
//           ErrorCode.StorageOperationFailed);
//     }
//   }
//
//   protected virtual void OnStateChanged(
//       string key,
//       object oldValue,
//       object newValue,
//       StateChangeType changeType)
//   {
//     try
//     {
//       StateChanged?.Invoke(this,
//           new StateChangedEventArgs(key, oldValue, newValue, changeType));
//     }
//     catch (Exception ex)
//     {
//       _logger.LogError(ex, "Error in state change handler");
//     }
//   }
//
//   public async ValueTask DisposeAsync()
//   {
//     if (_disposed) return;
//
//     await _lock.WaitAsync();
//     try
//     {
//       if (_disposed) return;
//       _disposed = true;
//
//       // Clear cache
//       _cache.Clear();
//
//       // Dispose resources
//       _lock.Dispose();
//     }
//     finally
//     {
//       _lock.Release();
//     }
//   }
//
//   protected virtual async Task InitializeAsync()
//   {
//     // Ensure required tables exist
//     await _data.ExecuteAsync(@"
//             CREATE TABLE IF NOT EXISTS state (
//                 id INTEGER PRIMARY KEY AUTOINCREMENT,
//                 key TEXT NOT NULL,
//                 value TEXT,
//                 change_type INTEGER NOT NULL,
//                 timestamp DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
//                 CONSTRAINT state_history UNIQUE (key, timestamp)
//             );
//
//             CREATE INDEX IF NOT EXISTS idx_state_key_time
//             ON state(key, timestamp DESC);");
//   }
// }
//
// /// <summary>
// /// Interface for state management operations
// /// </summary>
// public interface IGhostState : IAsyncDisposable
// {
//   Task<T> GetAsync<T>(string key);
//   Task SetAsync<T>(string key, T value);
//   Task DeleteAsync(string key);
//   Task<bool> ExistsAsync(string key);
//   Task<IEnumerable<StateEntry>> GetHistoryAsync(string key, int limit = 10);
//   event EventHandler<StateChangedEventArgs> StateChanged;
// }
