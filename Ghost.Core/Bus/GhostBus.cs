using Ghost.Data;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Ghost.Exceptions;
using MemoryPack;
using System.Threading.Channels;

namespace Ghost.Storage
{
    /// <summary>
    /// Message bus implementation for pub/sub communication using ICache as a backing store
    /// with MessagePack serialization for improved performance and smaller message size
    /// </summary>
    public class GhostBus : IGhostBus
    {
        private readonly ICache _cache;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _subscriptions = new();
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly ConcurrentDictionary<string, List<Action<string, string>>> _callbacks = new();
        private readonly Timer _cleanupTimer;
        private readonly object _lastTopicLock = new();
        private string _lastTopic;
        private bool _isDisposed;

        /// <summary>
        /// Create a new message bus using the specified cache
        /// </summary>
        /// <param name="cache">Cache provider for message storage</param>
        public GhostBus(ICache cache)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _cleanupTimer = new Timer(CleanupCallback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        /// <summary>
        /// Publish a message to the specified channel using MessagePack serialization
        /// </summary>
        public async Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(GhostBus));
            if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("Channel cannot be empty", nameof(channel));
            if (message == null) throw new ArgumentNullException(nameof(message));

            try
            {
                // Serialize message using MessagePack
                byte[] serialized = MemoryPackSerializer.Serialize(message);

                // Generate unique message ID (timestamp:guid)
                string messageId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{Guid.NewGuid()}";

                // Store message with channel prefix
                string key = $"message:{channel}:{messageId}";
                //expiry ??= TimeSpan.FromHours(1); // Default expiry time is 1 hour

                await _cache.SetAsync(key, serialized, expiry);

                // Store channel in active channels set
                await _cache.SetAsync("channels:active", channel);

                // Store latest message ID for the channel
                await _cache.SetAsync($"channel:{channel}:last", messageId);

                // Notify all subscribers
                NotifySubscribers(channel, messageId);
            }
            catch (Exception ex)
            {
                throw new GhostException("Failed to publish message", ex, ErrorCode.StorageConnectionFailed);
            }
        }

        /// <summary>
        /// Subscribe to messages on the specified channel or pattern
        /// </summary>
        public async IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(GhostBus));
            if (string.IsNullOrWhiteSpace(channelPattern)) throw new ArgumentException("Channel pattern cannot be empty", nameof(channelPattern));

            // Create subscription ID
            string subscriptionId = $"{channelPattern}:{Guid.NewGuid()}";

            // Create cancellation token source linked to the provided token
            var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Register subscription
            _subscriptions[subscriptionId] = linkedSource;

            // Create a channel to receive messages
            var channel = Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

            // Register callback for the pattern
            RegisterCallback(channelPattern, (topic, messageId) => channel.Writer.TryWrite((topic, messageId)));

            try
            {
                // Process any existing messages first (catch-up)
                await ProcessExistingMessagesAsync<T>(channelPattern, channel.Writer, linkedSource.Token);

                // Process new messages as they arrive
                while (!linkedSource.Token.IsCancellationRequested)
                {
                    if (await channel.Reader.WaitToReadAsync(linkedSource.Token))
                    {
                        while (channel.Reader.TryRead(out var item))
                        {
                            var (topic, messageId) = item;

                            // Store the last topic for reference
                            lock (_lastTopicLock)
                            {
                                _lastTopic = topic;
                            }

                            // Get the message from cache
                            string key = $"message:{topic}:{messageId}";
                            byte[]? data = await _cache.GetAsync<byte[]>(key, cancellationToken);

                            if (data is { Length: > 0 })
                            {
                                // Deserialize and yield the message using MessagePack
                                T message = MemoryPackSerializer.Deserialize<T>(data);
                                yield return message;
                            }
                        }
                    }
                }
            }
            finally
            {
                // Clean up subscription
                _subscriptions.TryRemove(subscriptionId, out _);
                linkedSource.Dispose();

                // Unregister callback
                UnregisterCallback(channelPattern);

                // Complete channel
                channel.Writer.TryComplete();
            }
        }

        /// <summary>
        /// Process existing messages for a channel pattern
        /// </summary>
        private async Task ProcessExistingMessagesAsync<T>(string channelPattern, ChannelWriter<(string, string)> writer, CancellationToken cancellationToken)
        {
            try
            {
                // Get active channels
                var keys = new List<string> { "channels:active" };
                var channels = await _cache.GetManyAsync<string>(keys, cancellationToken);

                // Filter channels that match the pattern
                var matchingChannels = channels.Where(ch
                        => MatchesPattern(ch.Key, channelPattern)).ToList();

                foreach (var channel in matchingChannels)
                {
                    // Get latest message ID for the channel
                    var key = string.Format("channel:{0}:last", channel);
                    string? lastMessageId = await _cache.GetAsync<string>(key, cancellationToken);

                    if (!string.IsNullOrEmpty(lastMessageId))
                    {
                        // Add to processing queue
                        writer.TryWrite((channel.Key, lastMessageId));
                    }
                }
            }
            catch (Exception ex)
            {
                G.LogWarn($"Error processing existing messages for pattern {channelPattern}: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribe from a channel or pattern
        /// </summary>
        public async Task UnsubscribeAsync(string channelPattern)
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(GhostBus));
            if (string.IsNullOrWhiteSpace(channelPattern)) throw new ArgumentException("Channel pattern cannot be empty", nameof(channelPattern));

            await _lock.WaitAsync();
            try
            {
                // Find all subscriptions for the pattern
                var subscriptionIds = _subscriptions.Keys.Where(k => k.StartsWith(channelPattern + ":")).ToList();

                // Cancel all subscriptions
                foreach (var id in subscriptionIds)
                {
                    if (_subscriptions.TryRemove(id, out var cts))
                    {
                        cts.Cancel();
                        cts.Dispose();
                    }
                }

                // Unregister callbacks
                UnregisterCallback(channelPattern);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Check if the message bus is available
        /// </summary>
        public async Task<bool> IsAvailableAsync()
        {
            if (_isDisposed) return false;

            try
            {
                // Try to set and get a test value
                string testKey = $"ghost:bus:test:{Guid.NewGuid()}";
                string testValue = Guid.NewGuid().ToString();

                await _cache.SetAsync(testKey, testValue, TimeSpan.FromSeconds(5));
                string result = await _cache.GetAsync<string>(testKey);

                return testValue == result;
            }
            catch (Exception ex)
            {
                G.LogWarn($"Message bus unavailable: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get the channel name from the last received message
        /// </summary>
        public string GetLastTopic()
        {
            lock (_lastTopicLock)
            {
                return _lastTopic;
            }
        }

        /// <summary>
        /// Register a callback for a channel pattern
        /// </summary>
        private void RegisterCallback(string channelPattern, Action<string, string> callback)
        {
            _callbacks.AddOrUpdate(
                channelPattern,
                new List<Action<string, string>> { callback },
                (_, existing) =>
                {
                    existing.Add(callback);
                    return existing;
                });
        }

        /// <summary>
        /// Unregister a callback for a channel pattern
        /// </summary>
        private void UnregisterCallback(string channelPattern)
        {
            _callbacks.TryRemove(channelPattern, out _);
        }

        /// <summary>
        /// Notify subscribers of a new message
        /// </summary>
        private void NotifySubscribers(string channel, string messageId)
        {
            // Find all matching pattern callbacks
            foreach (var (pattern, callbacks) in _callbacks)
            {
                if (MatchesPattern(channel, pattern))
                {
                    foreach (var callback in callbacks)
                    {
                        try
                        {
                            callback(channel, messageId);
                        }
                        catch (Exception ex)
                        {
                            G.LogWarn($"Error in subscription callback for {pattern}: {ex.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Check if a channel matches a pattern
        /// </summary>
        private bool MatchesPattern(string channel, string pattern)
        {
            // Exact match
            if (pattern == channel) return true;

            // No wildcards - no match
            if (!pattern.Contains('*')) return false;

            // Convert pattern to regex
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            return Regex.IsMatch(channel, regexPattern);
        }

        /// <summary>
        /// Clean up expired messages
        /// </summary>
        private async void CleanupCallback(object state)
        {
            if (_isDisposed) return;

            try
            {
                await _lock.WaitAsync();
                try
                {
                    // Remove subscriptions for completed tokens
                    var completedSubscriptions = _subscriptions
                        .Where(kv => kv.Value.IsCancellationRequested)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var id in completedSubscriptions)
                    {
                        if (_subscriptions.TryRemove(id, out var cts))
                        {
                            cts.Dispose();
                        }
                    }

                    // Note: We don't need to explicitly clean up messages as they have TTL set
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (Exception ex)
            {
                G.LogWarn($"Error in cleanup task: {ex.Message}");
            }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;

            await _lock.WaitAsync();
            try
            {
                if (_isDisposed) return;
                _isDisposed = true;

                // Cancel all subscriptions
                foreach (var (_, cts) in _subscriptions)
                {
                    cts.Cancel();
                    cts.Dispose();
                }

                _subscriptions.Clear();
                _callbacks.Clear();

                // Clean up timer
                await _cleanupTimer.DisposeAsync();

                // Clean up lock
                _lock.Dispose();
            }
            finally
            {
                if (!_isDisposed)
                {
                    _lock.Release();
                }
            }
        }
    }
}