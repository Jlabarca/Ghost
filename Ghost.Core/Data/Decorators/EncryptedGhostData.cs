using System.Security.Cryptography;
using System.Text.Json;
using Ghost.Config;
using Ghost.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
namespace Ghost.Data;

/// <summary>
///     Decorator that adds encryption to sensitive data before storing it.
///     Keys with the prefix "secure:" will be encrypted before storage and decrypted on retrievaG.
/// </summary>
public class EncryptedGhostData : IGhostData
{

    /// <summary>
    ///     The prefix used to identify keys that should be encrypted.
    /// </summary>
    public const string SecureKeyPrefix = "secure:";
    private readonly Lazy<Aes> _aesProvider;
    private readonly IOptions<SecurityDataConfig> _config;
    private readonly IGhostData _inner;
    private readonly IGhostLogger _logger;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EncryptedGhostData" /> class.
    /// </summary>
    /// <param name="inner">The decorated IGhostData implementation.</param>
    /// <param name="config">The security configuration.</param>
    /// <param name="logger">The logger.</param>
    public EncryptedGhostData(
            IGhostData inner,
            IOptions<SecurityDataConfig> config,
            IGhostLogger logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Initialize the AES provider lazily
        _aesProvider = new Lazy<Aes>(() =>
        {
            Aes? provider = Aes.Create();
            provider.Key = Convert.FromBase64String(_config.Value.EncryptionKey);
            provider.IV = Convert.FromBase64String(_config.Value.EncryptionIV);
            return provider;
        });
    }

    /// <inheritdoc />
    public IDatabaseClient GetDatabaseClient()
    {
        return _inner.GetDatabaseClient();
    }

#region Transaction Support

    /// <inheritdoc />
    public async Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // Transactions pass through to the inner implementation
        IGhostTransaction transaction = await _inner.BeginTransactionAsync(ct);

        // We don't currently wrap transactions with encryption
        return transaction;
    }

#endregion

#region IAsyncDisposable

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_aesProvider.IsValueCreated)
        {
            _aesProvider.Value.Dispose();
        }

        await _inner.DisposeAsync();
    }

#endregion

#region Key-Value Operations

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // If the key is for secure data, we need to decrypt it
        if (key.StartsWith(SecureKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                // Get the encrypted value as byte array
                byte[]? encryptedValue = await _inner.GetAsync<byte[]>(key, ct);
                if (encryptedValue == null || encryptedValue.Length == 0)
                {
                    return default(T?);
                }

                // Decrypt the value
                string decryptedJson = DecryptValue(encryptedValue);

                // Deserialize the decrypted JSON
                return JsonSerializer.Deserialize<T>(decryptedJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt value for key {Key}", key);
                throw new GhostSecurityException("Failed to decrypt secure data", ex);
            }
        }

        // For non-secure keys, just pass through
        return await _inner.GetAsync<T>(key, ct);
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        // If the key is for secure data, we need to encrypt it
        if (key.StartsWith(SecureKeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (value == null)
                {
                    // Null values don't need encryption
                    await _inner.SetAsync<byte[]>(key, null, expiry, ct);
                    return;
                }

                // Serialize the value to JSON
                string json = JsonSerializer.Serialize(value);

                // Encrypt the JSON
                byte[] encryptedValue = EncryptValue(json);

                // Store the encrypted value
                await _inner.SetAsync(key, encryptedValue, expiry, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to encrypt value for key {Key}", key);
                throw new GhostSecurityException("Failed to encrypt secure data", ex);
            }
        }
        else
        {
            // For non-secure keys, just pass through
            await _inner.SetAsync(key, value, expiry, ct);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.DeleteAsync(key, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.ExistsAsync(key, ct);
    }

#endregion

#region Batch Key-Value Operations

    /// <inheritdoc />
    public async Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        var result = new Dictionary<string, T?>();
        var secureKeys = new List<string>();
        var nonSecureKeys = new List<string>();

        // Separate secure and non-secure keys
        foreach (string key in keys)
        {
            if (key.StartsWith(SecureKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                secureKeys.Add(key);
            }
            else
            {
                nonSecureKeys.Add(key);
            }
        }

        // Process non-secure keys normally
        if (nonSecureKeys.Count > 0)
        {
            var nonSecureResults = await _inner.GetBatchAsync<T>(nonSecureKeys, ct);
            foreach (var kv in nonSecureResults)
            {
                result[kv.Key] = kv.Value;
            }
        }

        // Process secure keys one by one (batch decryption could be added later)
        foreach (string key in secureKeys)
        {
            try
            {
                result[key] = await GetAsync<T>(key, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decrypt batch value for key {Key}", key);
                result[key] = default(T?);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();

        var secureItems = new Dictionary<string, T>();
        var nonSecureItems = new Dictionary<string, T>();

        // Separate secure and non-secure keys
        foreach (string key in items.Keys)
        {
            if (key.StartsWith(SecureKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                secureItems[key] = items[key];
            }
            else
            {
                nonSecureItems[key] = items[key];
            }
        }

        // Process non-secure items normally
        if (nonSecureItems.Count > 0)
        {
            await _inner.SetBatchAsync(nonSecureItems, expiry, ct);
        }

        // Process secure items one by one (batch encryption could be added later)
        foreach (var kv in secureItems)
        {
            await SetAsync(kv.Key, kv.Value, expiry, ct);
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.DeleteBatchAsync(keys, ct);
    }

#endregion

#region SQL Operations

    /// <inheritdoc />
    public async Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.QuerySingleAsync<T>(sql, param, ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.QueryAsync<T>(sql, param, ct);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.ExecuteAsync(sql, param, ct);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.ExecuteBatchAsync(commands, ct);
    }

#endregion

#region Schema Operations

    /// <inheritdoc />
    public async Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.TableExistsAsync(tableName, ct);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default(CancellationToken))
    {
        ThrowIfDisposed();
        return await _inner.GetTableNamesAsync(ct);
    }

#endregion

#region Encryption Helpers

    /// <summary>
    ///     Encrypts a string value using AES encryption.
    /// </summary>
    /// <param name="value">The value to encrypt.</param>
    /// <returns>The encrypted value as a byte array.</returns>
    private byte[] EncryptValue(string value)
    {
        using Aes? aes = _aesProvider.Value;
        using ICryptoTransform? encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using MemoryStream? ms = new MemoryStream();
        using (CryptoStream? cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (StreamWriter? sw = new StreamWriter(cs))
        {
            sw.Write(value);
        }

        return ms.ToArray();
    }

    /// <summary>
    ///     Decrypts a byte array to a string value using AES encryption.
    /// </summary>
    /// <param name="encryptedValue">The encrypted value.</param>
    /// <returns>The decrypted value as a string.</returns>
    private string DecryptValue(byte[] encryptedValue)
    {
        using Aes? aes = _aesProvider.Value;
        using ICryptoTransform? decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using MemoryStream? ms = new MemoryStream(encryptedValue);
        using CryptoStream? cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using StreamReader? sr = new StreamReader(cs);

        return sr.ReadToEnd();
    }

    /// <summary>
    ///     Throws if this object has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EncryptedGhostData));
        }
    }

#endregion
}
/// <summary>
///     Exception thrown when a security-related operation fails.
/// </summary>
public class GhostSecurityException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="GhostSecurityException" /> class.
    /// </summary>
    public GhostSecurityException() : base("A security operation failed")
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="GhostSecurityException" /> class with the specified message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public GhostSecurityException(string message) : base(message)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="GhostSecurityException" /> class with the specified message and inner
    ///     exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that is the cause of the current exception.</param>
    public GhostSecurityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
