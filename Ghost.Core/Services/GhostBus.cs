using Ghost.Core.Data;
using Ghost.Core.Exceptions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Ghost.Core.Storage;

/// <summary>
/// Interface for message bus operations
/// </summary>
public interface IGhostBus : IAsyncDisposable
{
    Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null);
    IAsyncEnumerable<T> SubscribeAsync<T>(string channel, CancellationToken ct = default);
    Task UnsubscribeAsync(string channel);
    Task<long> GetSubscriberCountAsync(string channel);
    Task<IEnumerable<string>> GetActiveChannelsAsync();
    Task ClearChannelAsync(string channel);
    Task<bool> IsAvailableAsync();

    Task UnsubscribeAllAsync();
}

/// <summary>
/// Message bus implementation using the cache system for pub/sub
/// </summary>
public class GhostBus : IGhostBus
{
    private readonly ICache _cache;
    private readonly ConcurrentDictionary<string, ConcurrentBag<ChannelMessageQueue>> _subscriptions;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public GhostBus(ICache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _subscriptions = new ConcurrentDictionary<string, ConcurrentBag<ChannelMessageQueue>>();
    }

    public async Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel))
            throw new ArgumentException("Channel cannot be empty", nameof(channel));

        try
        {
            var serialized = JsonSerializer.Serialize(message);
            var messageId = Guid.NewGuid().ToString();
            var messageKey = $"message:{channel}:{messageId}";

            // Store message
            await _cache.SetAsync(messageKey, serialized, expiry ?? TimeSpan.FromHours(1));

            // Notify subscribers
            if (_subscriptions.TryGetValue(channel, out var queues))
            {
                foreach (var queue in queues)
                {
                    queue.Enqueue(serialized);
                }
            }

            G.LogDebug("Published message to channel: {0}", channel);
        }
        catch (Exception ex)
        {
            G.LogError("Failed to publish message to channel: {0}", ex, channel);
            throw new GhostException(
                $"Failed to publish message to channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async IAsyncEnumerable<T> SubscribeAsync<T>(
        string channel,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel))
            throw new ArgumentException("Channel cannot be empty", nameof(channel));

        var queue = new ChannelMessageQueue();

        try
        {
            // Add subscription
            var queues = _subscriptions.GetOrAdd(channel, _ => new ConcurrentBag<ChannelMessageQueue>());
            queues.Add(queue);

            G.LogDebug("Subscribed to channel: {0}", channel);

            while (!ct.IsCancellationRequested)
            {
                T item = default;
                if (queue.TryDequeue(out var message))
                {
                    try
                    {
                        item = JsonSerializer.Deserialize<T>(message) ?? throw new InvalidOperationException();
                    }
                    catch (JsonException ex)
                    {
                        G.LogWarn("Failed to deserialize message from channel {0}: {1}", channel, ex.Message);
                    }

                    yield return item;
                }
                else
                {
                    await Task.Delay(100, ct);
                }
            }
        }
        finally
        {
            // Clean up subscription
            if (_subscriptions.TryGetValue(channel, out var existingQueues))
            {
                var updated = new ConcurrentBag<ChannelMessageQueue>(
                    existingQueues.Where(q => q != queue));

                if (updated.IsEmpty)
                {
                    _subscriptions.TryRemove(channel, out _);
                }
                else
                {
                    _subscriptions.TryUpdate(channel, updated, existingQueues);
                }
            }
        }
    }

    public async Task UnsubscribeAsync(string channel)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel))
            throw new ArgumentException("Channel cannot be empty", nameof(channel));

        await _lock.WaitAsync();
        try
        {
            _subscriptions.TryRemove(channel, out _);
            G.LogDebug("Unsubscribed from channel: {0}", channel);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<long> GetSubscriberCountAsync(string channel)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel))
            throw new ArgumentException("Channel cannot be empty", nameof(channel));

        var count = 0L;

        // Count local subscribers
        if (_subscriptions.TryGetValue(channel, out var queues))
        {
            count += queues.Count;
        }

        // Count remote subscribers (if using Redis)
        try
        {
            var remoteCount = await _cache.GetAsync<long>($"subscribers:{channel}");
            count += remoteCount;
        }
        catch (Exception ex)
        {
            G.LogError("Failed to get remote subscriber count for channel: {0}", ex, channel);
        }

        return count;
    }

    public async Task<IEnumerable<string>> GetActiveChannelsAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));

        var channels = new HashSet<string>(_subscriptions.Keys);

        try
        {
            // Get remote channels (if using Redis)
            var remoteChannels = await _cache.GetAsync<HashSet<string>>("active_channels");
            if (remoteChannels != null)
            {
                channels.UnionWith(remoteChannels);
            }
        }
        catch (Exception ex)
        {
            G.LogError("Failed to get remote active channels", ex);
        }

        return channels;
    }

    public async Task ClearChannelAsync(string channel)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel))
            throw new ArgumentException("Channel cannot be empty", nameof(channel));

        try
        {
            // Clear messages
            await _cache.DeleteAsync($"message:{channel}:*");

            // Clear subscriber count
            await _cache.DeleteAsync($"subscribers:{channel}");

            // Clear local subscriptions
            _subscriptions.TryRemove(channel, out _);

            G.LogDebug("Cleared channel: {0}", channel);
        }
        catch (Exception ex)
        {
            G.LogError("Failed to clear channel: {0}", ex, channel);
            throw new GhostException(
                $"Failed to clear channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }
    public Task<bool> IsAvailableAsync()
    {
        return Task.FromResult(true);
    }
    public Task UnsubscribeAllAsync()
    {
        return _disposed ?
                throw new ObjectDisposedException(nameof(GhostBus)) :
                Task.WhenAll(_subscriptions.Keys.Select(UnsubscribeAsync));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;

            _subscriptions.Clear();

            if (_cache is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}