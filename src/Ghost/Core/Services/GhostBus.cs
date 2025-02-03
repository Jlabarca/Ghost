// Ghost.Core.Services/Bus/GhostBus.cs
using Ghost.Core.Services.Bus;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>
/// Interface for message bus operations
/// </summary>
public interface IGhostBus : IAsyncDisposable
{
    Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null);
    IAsyncEnumerable<T> SubscribeAsync<T>(string channel, CancellationToken ct = default);
    Task<long> GetSubscriberCountAsync(string channel);
    Task<IEnumerable<string>> GetActiveChannelsAsync();
    Task UnsubscribeAsync(string channel);
    Task ClearChannelAsync(string channel);
}

/// <summary>
/// Message bus implementation supporting both Redis and in-memory modes
/// </summary>
public class GhostBus : IGhostBus
{
    private readonly IRedisClient _client;
    private readonly ILogger<GhostBus> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, List<IAsyncDisposable>> _subscriptions;
    private bool _disposed;

    public GhostBus(IRedisClient client, ILogger<GhostBus> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _subscriptions = new ConcurrentDictionary<string, List<IAsyncDisposable>>();
    }

    public async Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel)) throw new ArgumentException("Channel cannot be empty", nameof(channel));

        try
        {
            var serialized = JsonSerializer.Serialize(message);
            await _client.PublishAsync(channel, serialized);

            // Store message if expiry is specified
            if (expiry.HasValue)
            {
                var messageKey = $"message:{channel}:{Guid.NewGuid()}";
                await _client.SetAsync(messageKey, serialized, expiry.Value);
            }

            _logger.LogTrace("Published message to channel: {Channel}", channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish message to channel: {Channel}", channel);
            throw new GhostException(
                $"Failed to publish message to channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async IAsyncEnumerable<T> SubscribeAsync<T>(
        string channel,
        [System.Runtime.CompilerServices.EnumeratorCancellation] 
        CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel)) throw new ArgumentException("Channel cannot be empty", nameof(channel));
        T deserialized = default;

        try
        {
            _logger.LogDebug("Subscribing to channel: {Channel}", channel);

            // Track subscription
            var subscription = new BusSubscription(_client, channel);
            _subscriptions.AddOrUpdate(
                channel,
                new List<IAsyncDisposable> { subscription },
                (_, list) => { list.Add(subscription); return list; }
            );

            await foreach (var message in _client.SubscribeAsync(channel, ct))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (string.IsNullOrEmpty(message))
                {
                    continue;
                }

                try
                {
                    deserialized = JsonSerializer.Deserialize<T>(message);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize message from channel {Channel}", channel);
                    continue;
                }


            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Subscription cancelled for channel: {Channel}", channel);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in subscription to channel: {Channel}", channel);
            throw new GhostException(
                $"Subscription failed for channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }

        if (deserialized != null)
        {
            yield return deserialized;
        }
    }

    public async Task<long> GetSubscriberCountAsync(string channel)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel)) throw new ArgumentException("Channel cannot be empty", nameof(channel));

        try
        {
            var count = await _client.GetAsync<long>($"subscribers:{channel}");

            if (_subscriptions.TryGetValue(channel, out var subs))
            {
                count += subs.Count;
            }

            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get subscriber count for channel: {Channel}", channel);
            throw new GhostException(
                $"Failed to get subscriber count for channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task<IEnumerable<string>> GetActiveChannelsAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));

        try
        {
            var channels = new HashSet<string>();

            // Get channels from Redis
            var redisChannels = await _client.GetAsync<HashSet<string>>("active_channels");
            if (redisChannels != null)
            {
                channels.UnionWith(redisChannels);
            }

            // Add local subscriptions
            channels.UnionWith(_subscriptions.Keys);

            return channels;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active channels");
            throw new GhostException(
                "Failed to get active channels",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async Task UnsubscribeAsync(string channel)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel)) throw new ArgumentException("Channel cannot be empty", nameof(channel));

        await _lock.WaitAsync();
        try
        {
            if (_subscriptions.TryRemove(channel, out var subs))
            {
                foreach (var sub in subs)
                {
                    await sub.DisposeAsync();
                }
            }

            _logger.LogDebug("Unsubscribed from channel: {Channel}", channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unsubscribe from channel: {Channel}", channel);
            throw new GhostException(
                $"Failed to unsubscribe from channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearChannelAsync(string channel)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostBus));
        if (string.IsNullOrEmpty(channel)) throw new ArgumentException("Channel cannot be empty", nameof(channel));

        try
        {
            // Remove stored messages
            await _client.DeleteAsync($"message:{channel}:*");
            
            // Remove subscriber count
            await _client.DeleteAsync($"subscribers:{channel}");
            
            _logger.LogDebug("Cleared channel: {Channel}", channel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear channel: {Channel}", channel);
            throw new GhostException(
                $"Failed to clear channel: {channel}",
                ex,
                ErrorCode.StorageOperationFailed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;

            // Dispose all subscriptions
            foreach (var subs in _subscriptions.Values)
            {
                foreach (var sub in subs)
                {
                    await sub.DisposeAsync();
                }
            }
            _subscriptions.Clear();

            if (_client is IAsyncDisposable disposable)
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

// Ghost.Core.Services/Bus/BusSubscription.cs
namespace Ghost.Core.Services.Bus;

internal class BusSubscription : IAsyncDisposable
{
    private readonly IRedisClient _client;
    private readonly string _channel;
    private bool _disposed;

    public BusSubscription(IRedisClient client, string channel)
    {
        _client = client;
        _channel = channel;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _client.DeleteAsync($"subscribers:{_channel}");
    }
}