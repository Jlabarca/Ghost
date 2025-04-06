using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ghost.Core.Data;

public class LocalCache : ICache
{
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CacheEntry<object>> _cache;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks;
    private readonly SemaphoreSlim _globalLock = new SemaphoreSlim(1, 1);
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    public LocalCache(string basePath)
    {
        _cachePath = Path.Combine(basePath, "cache");
        _cache = new ConcurrentDictionary<string, CacheEntry<object>>();
        _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
        _cleanupTimer = new Timer(CleanupCallback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        Directory.CreateDirectory(_cachePath);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(!_disposed);

    public Task<long> GetStorageSizeAsync(CancellationToken ct = default)
    {
        var directoryInfo = new DirectoryInfo(_cachePath);
        return Task.FromResult(directoryInfo.EnumerateFiles().Sum(file => file.Length));
    }

    public async Task<T> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

        // Check memory cache first
        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            return (T)entry.Value;
        }

        // Try load from disk
        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath(key);
            if (!File.Exists(filePath)) return default;

            var json = await File.ReadAllTextAsync(filePath, ct);
            var diskEntry = JsonSerializer.Deserialize<CacheEntry<T>>(json);

            if (diskEntry?.IsExpired != false)
            {
                File.Delete(filePath);
                return default;
            }

            var typedEntry = new CacheEntry<object>
            {
                Value = diskEntry.Value,
                ExpiresAt = diskEntry.ExpiresAt,
                TypeName = typeof(T).FullName ?? "unknown"
            };

            _cache.TryAdd(key, typedEntry);
            return diskEntry.Value;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

        var entry = new CacheEntry<object>
        {
            Value = value,
            ExpiresAt = expiry.HasValue ? DateTime.UtcNow + expiry.Value : null,
            TypeName = typeof(T).FullName ?? "unknown"
        };

        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            _cache[key] = entry;

            var json = JsonSerializer.Serialize(entry);
            var filePath = GetFilePath(key);
            var tempPath = Path.GetTempFileName();

            await File.WriteAllTextAsync(tempPath, json, ct);
            File.Move(tempPath, filePath, true);

            return true;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            _cache.TryRemove(key, out _);
            var filePath = GetFilePath(key);

            if (!File.Exists(filePath)) return false;
            File.Delete(filePath);
            return true;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

        if (_cache.TryGetValue(key, out var entry) && !entry.IsExpired)
        {
            return true;
        }

        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            var filePath = GetFilePath(key);
            return File.Exists(filePath);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<bool> ExpireAsync(string key, TimeSpan expiry, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalCache));

        var fileLock = GetFileLock(key);
        await fileLock.WaitAsync(ct);
        try
        {
            if (!_cache.TryGetValue(key, out var entry))
            {
                var filePath = GetFilePath(key);
                if (!File.Exists(filePath)) return false;

                var json = await File.ReadAllTextAsync(filePath, ct);
                entry = JsonSerializer.Deserialize<CacheEntry<object>>(json);
                if (entry == null) return false;
            }

            entry.ExpiresAt = DateTime.UtcNow + expiry;
            return true;
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

            foreach (var file in Directory.EnumerateFiles(_cachePath, "*.cache"))
            {
                File.Delete(file);
            }
        }
        finally
        {
            _globalLock.Release();
        }
    }
    public async Task<List<T>> GetAllAsync<T>(T channelsActive)
    {
        return await Task.FromResult(_cache.Values
            .Where(entry => entry.TypeName == typeof(T).FullName)
            .Select(entry => (T)entry.Value)
            .ToList());
    }

    private SemaphoreSlim GetFileLock(string key) =>
        _fileLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    private string GetFilePath(string key)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        var fileName = Convert.ToBase64String(hash)
            .Replace('/', '_')
            .Replace('+', '-')
            .TrimEnd('=');
        return Path.Combine(_cachePath, $"{fileName}.cache");
    }

    private async void CleanupCallback(object state)
    {
        await _globalLock.WaitAsync();
        try
        {
            // Cleanup expired memory entries
            var expiredKeys = _cache
                .Where(kvp => kvp.Value.IsExpired)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                await DeleteAsync(key);
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

            foreach (var fileLock in _fileLocks.Values)
            {
                fileLock.Dispose();
            }
            _fileLocks.Clear();

            _globalLock.Dispose();
        }
        finally
        {
            _globalLock.Release();
        }
    }
}