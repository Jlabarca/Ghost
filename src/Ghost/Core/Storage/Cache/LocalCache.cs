using Ghost.Core.Data;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Ghost.Core.Storage.Cache;

public class LocalCache : ICache
{
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks;
    private readonly SemaphoreSlim _globalLock = new(1, 1);
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    private class CacheEntry
    {
        public object Value { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string TypeName { get; set; }
        public bool IsExpired => ExpiresAt.HasValue && DateTime.UtcNow > ExpiresAt.Value;
    }

    public LocalCache(string optionsDataDirectory)
    {
        _cachePath = Path.Combine(optionsDataDirectory, "cache");
        _cache = new ConcurrentDictionary<string, CacheEntry>();
        _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
        _cleanupTimer = new Timer(CleanupCallback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        Directory.CreateDirectory(_cachePath);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return Task.FromResult(!_disposed);
    }

    public Task<long> GetStorageSizeAsync(CancellationToken ct = default)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(_cachePath);
            return Task.FromResult(directoryInfo.EnumerateFiles().Sum(file => file.Length));
        }
        catch (Exception ex)
        {
            G.LogError("Failed to get storage size", ex);
            return Task.FromResult(0L);
        }
    }

    private SemaphoreSlim GetFileLock(string key)
    {
        return _fileLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(key);

        // Check memory cache first
        if (_cache.TryGetValue(key, out var entry))
        {
            if (!entry.IsExpired)
            {
                try
                {
                    if (entry.Value is T typedValue)
                    {
                        return typedValue;
                    }
                    var json = JsonSerializer.Serialize(entry.Value);
                    return JsonSerializer.Deserialize<T>(json);
                }
                catch (Exception ex)
                {
                    G.LogError("Failed to deserialize cache value for key: {0}", ex, key);
                }
            }
            else
            {
                // Remove expired entry
                _cache.TryRemove(key, out _);
            }
        }

        // Try load from disk
        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
            {
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(fileStream);
                var json = await reader.ReadToEndAsync(ct);

                var diskEntry = JsonSerializer.Deserialize<CacheEntry>(json);
                if (diskEntry != null && !diskEntry.IsExpired)
                {
                    _cache.TryAdd(key, diskEntry);
                    return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(diskEntry.Value));
                }
                else
                {
                    // Remove expired file
                    fileStream.Close();
                    File.Delete(filePath);
                }
            }
        }
        catch (Exception ex)
        {
            G.LogError("Failed to load cache from disk for key: {0}", ex, key);
        }
        finally
        {
            fileLock.Release();
        }

        return default;
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(key);

        var entry = new CacheEntry
        {
            Value = value,
            ExpiresAt = expiry.HasValue ? DateTime.UtcNow + expiry.Value : null,
            TypeName = typeof(T).FullName
        };

        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            _cache[key] = entry;

            // Persist to disk with proper file handling
            var filePath = GetFilePath(key);
            var json = JsonSerializer.Serialize(entry);
            var tempPath = Path.GetTempFileName();

            // Write to temp file first
            await File.WriteAllTextAsync(tempPath, json, ct);

            // Move temp file to final destination (atomic operation)
            File.Move(tempPath, filePath, true);

            G.LogDebug("Cache set for key: {0}", key);
            return true;
        }
        catch (Exception ex)
        {
            G.LogError("Failed to set cache value for key: {0}", ex, key);
            return false;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(key);

        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            _cache.TryRemove(key, out _);

            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            G.LogDebug("Cache deleted for key: {0}", key);
            return true;
        }
        catch (Exception ex)
        {
            G.LogError("Failed to delete cache value for key: {0}", ex, key);
            return false;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(key);

        // Check memory cache first
        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            return true;
        }

        // Check disk
        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath(key);
            if (!File.Exists(filePath)) return false;

            // Verify it's not expired
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(fileStream);
            var json = await reader.ReadToEndAsync(ct);
            var diskEntry = JsonSerializer.Deserialize<CacheEntry>(json);

            return diskEntry != null && !diskEntry.IsExpired;
        }
        catch (Exception ex)
        {
            G.LogError("Failed to check cache existence for key: {0}", ex, key);
            return false;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<long> IncrementAsync(string key, long value = 1, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(key);

        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            var current = await GetAsync<long>(key, ct);
            var newValue = current + value;
            await SetAsync(key, newValue, ct: ct);
            return newValue;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<bool> ExpireAsync(string key, TimeSpan expiry, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));
        ValidateKey(key);

        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(key, out var entry))
            {
                var filePath = GetFilePath(key);
                if (!File.Exists(filePath)) return false;

                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var reader = new StreamReader(fileStream);
                var json = await reader.ReadToEndAsync(ct);
                entry = JsonSerializer.Deserialize<CacheEntry>(json);
                if (entry == null) return false;
            }

            entry.ExpiresAt = DateTime.UtcNow + expiry;
            return await SetAsync(key, entry.Value, expiry, ct);
        }
        catch (Exception ex)
        {
            G.LogError("Failed to set expiry for key: {0}", ex, key);
            return false;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

        await _globalLock.WaitAsync(ct);
        try
        {
            _cache.Clear();

            // Clear disk cache
            var files = Directory.GetFiles(_cachePath, "*.cache");
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    G.LogError("Failed to delete cache file: {0}", ex, file);
                }
            }

            G.LogInfo("Cache cleared");
        }
        finally
        {
            _globalLock.Release();
        }
    }

    private async void CleanupCallback(object state)
    {
        await _globalLock.WaitAsync();
        try
        {
            var expiredKeys = _cache
                .Where(kvp => kvp.Value.IsExpired)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                await DeleteAsync(key);
            }

            if (expiredKeys.Any())
            {
                G.LogDebug("Cleaned up {0} expired cache entries", expiredKeys.Count);
            }

            // Cleanup unused file locks
            var unusedLocks = _fileLocks.Keys.Except(_cache.Keys).ToList();
            foreach (var key in unusedLocks)
            {
                if (_fileLocks.TryRemove(key, out var fileLock))
                {
                    fileLock.Dispose();
                }
            }
        }
        finally
        {
            _globalLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _globalLock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;

            _cleanupTimer.Dispose();
            _cache.Clear();

            // Dispose all file locks
            foreach (var fileLock in _fileLocks.Values)
            {
                fileLock.Dispose();
            }
            _fileLocks.Clear();

            _globalLock.Dispose();
            G.LogInfo("LocalCache disposed");
        }
        finally
        {
            _globalLock.Release();
        }
    }

    private string GetFilePath(string key)
    {
        var hash = HashKey(key);
        return Path.Combine(_cachePath, $"{hash}.cache");
    }

    private static string HashKey(string key)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(key);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToBase64String(hash)
            .Replace('/', '_')
            .Replace('+', '-')
            .TrimEnd('=');
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be empty", nameof(key));
    }
}