using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Ghost.Core.Exceptions;
using MemoryPack;
using StackExchange.Redis;

namespace Ghost.Core.Storage
{
  /// <summary>
  /// Message priority levels for prioritized message delivery
  /// </summary>
  public enum MessagePriority
  {
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
  }

  /// <summary>
  /// Extended GhostBus implementation using Redis as the primary transport
  /// with fallback mechanisms for enhanced reliability
  /// </summary>
  public class RedisGhostBus : IGhostBus, IDisposable, IAsyncDisposable
  {
    private readonly IConnectionMultiplexer _redis;
    private readonly ISubscriber _subscriber;
    private readonly IDatabase _db;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _subscriptions = new();
    private readonly ConcurrentDictionary<string, List<Action<string, string>>> _callbacks = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Timer _cleanupTimer;
    private readonly object _lastTopicLock = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastMessageTimes = new();
    private readonly CircuitBreaker _circuitBreaker;

    // Local persistence for offline operation
    private readonly IPersistentStorage _persistentStorage;
    private readonly Timer _persistenceFlushTimer;

    // Message TTLs by priority
    private readonly Dictionary<MessagePriority, TimeSpan> _defaultExpiries = new()
    {
        [MessagePriority.Low] = TimeSpan.FromHours(1),
        [MessagePriority.Normal] = TimeSpan.FromHours(6),
        [MessagePriority.High] = TimeSpan.FromHours(24),
        [MessagePriority.Critical] = TimeSpan.FromDays(7)
    };

    private string _lastTopic;
    private bool _isDisposed;
    private bool _isAvailable = false;
    private int _messageCounter = 0;

    // Connection monitoring
    private readonly Timer _connectionMonitorTimer;
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    /// <summary>
    /// Current connection state
    /// </summary>
    public ConnectionState ConnectionState => _connectionState;

    /// <summary>
    /// Event fired when connection state changes
    /// </summary>
    public event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

    /// <summary>
    /// Event fired when a message is published
    /// </summary>
    public event EventHandler<MessagePublishedEventArgs> MessagePublished;

    /// <summary>
    /// Create a new Redis-backed message bus
    /// </summary>
    /// <param name="connectionString">Redis connection string</param>
    public RedisGhostBus(string connectionString)
    {
      // Configure Redis
      var options = ConfigurationOptions.Parse(connectionString);
      options.AbortOnConnectFail = false; // Don't throw on initial connection failure
      options.ConnectRetry = 5; // Retry connection up to 5 times
      options.ConnectTimeout = 5000; // 5 second connection timeout

      _redis = ConnectionMultiplexer.Connect(options);
      _subscriber = _redis.GetSubscriber();
      _db = _redis.GetDatabase();

      // Set up circuit breaker
      _circuitBreaker = new CircuitBreaker(
          maxFailures: 3, // Trip after 3 consecutive failures
          resetTimeout: TimeSpan.FromSeconds(15) // Try to recover after 15 seconds
      );

      // Initialize persistent storage
      //_persistentStorage = persistentStorage;

      // Set up timers
      _cleanupTimer = new Timer(CleanupCallback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
      _connectionMonitorTimer = new Timer(MonitorConnectionAsync, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

      if (_persistentStorage != null)
      {
        _persistenceFlushTimer = new Timer(FlushPersistedMessagesAsync, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
      }

      // Subscribe to Redis connection events
      _redis.ConnectionFailed += OnRedisConnectionFailed;
      _redis.ConnectionRestored += OnRedisConnectionRestored;

      // Initial connection state check
      _ = CheckConnectionAsync();
    }

    private async void OnRedisConnectionFailed(object sender, ConnectionFailedEventArgs args)
    {
      await UpdateConnectionStateAsync(ConnectionState.Disconnected, args.Exception?.Message);
      G.LogWarn($"Redis connection failed: {args.Exception?.Message}");
    }

    private async void OnRedisConnectionRestored(object sender, ConnectionFailedEventArgs args)
    {
      await CheckConnectionAsync();
      G.LogInfo("Redis connection restored");
    }

    private async Task CheckConnectionAsync()
    {
      try
      {
        if (!_redis.IsConnected)
        {
          await UpdateConnectionStateAsync(ConnectionState.Disconnected, "Redis not connected");
          return;
        }

        // Ping Redis to verify connection
        var pingResult = await _db.PingAsync();
        bool available = pingResult != TimeSpan.MaxValue && pingResult.TotalMilliseconds < 1000;

        if (available)
        {
          await UpdateConnectionStateAsync(ConnectionState.Connected, null);
          _isAvailable = true;
          _circuitBreaker.OnSuccess();
        } else
        {
          await UpdateConnectionStateAsync(ConnectionState.Degraded, "Redis ping timeout");
          _isAvailable = false;
          _circuitBreaker.OnFailure();
        }
      }
      catch (Exception ex)
      {
        await UpdateConnectionStateAsync(ConnectionState.Disconnected, ex.Message);
        _isAvailable = false;
        _circuitBreaker.OnFailure();
        G.LogWarn($"Redis connection check failed: {ex.Message}");
      }
    }

    private async void MonitorConnectionAsync(object state)
    {
      await CheckConnectionAsync();
    }

    private async Task UpdateConnectionStateAsync(ConnectionState newState, string reason)
    {
      if (newState == _connectionState) return;

      var oldState = _connectionState;
      _connectionState = newState;

      // Notify listeners
      ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, newState, reason));

      // Log connection state change
      G.LogInfo($"RedisGhostBus connection state changed: {oldState} -> {newState} ({reason ?? "No reason"})");
    }

    /// <summary>
    /// Publish a message to the specified channel
    /// </summary>
    public async Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
    {
      await PublishWithPriorityAsync(channel, message, MessagePriority.Normal, expiry);
    }

    /// <summary>
    /// Publish a message with specified priority
    /// </summary>
    public async Task PublishWithPriorityAsync<T>(string channel, T message, MessagePriority priority, TimeSpan? expiry = null)
    {
      if (_isDisposed) throw new ObjectDisposedException(nameof(RedisGhostBus));
      if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("Channel cannot be empty", nameof(channel));
      if (message == null) throw new ArgumentNullException(nameof(message));

      // Apply default expiry based on priority if not specified
      expiry ??= _defaultExpiries.TryGetValue(priority, out var defaultExpiry) ? defaultExpiry : TimeSpan.FromHours(1);

      try
      {
        // Serialize message using MemoryPack
        byte[] serialized = MemoryPackSerializer.Serialize(message);

        // Generate unique message ID with counter for better ordering
        int counter = Interlocked.Increment(ref _messageCounter);
        string messageId = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}:{counter}:{Guid.NewGuid()}";

        // Store persistence metadata
        var metadata = new MessageMetadata
        {
            Id = messageId,
            Channel = channel,
            Priority = priority,
            Timestamp = DateTime.UtcNow,
            Expiry = DateTime.UtcNow.Add(expiry.Value),
            TypeName = typeof(T).FullName
        };

        if (_circuitBreaker.IsAllowed)
        {
          try
          {
            // Use Redis transaction for atomic operations
            var transaction = _db.CreateTransaction();

            // Store the message with automatic expiry
            string messageKey = $"message:{channel}:{messageId}";
            transaction.StringSetAsync(messageKey, serialized, expiry);

            // Store latest message ID for the channel
            transaction.StringSetAsync($"channel:{channel}:last", messageId);

            // Add channel to active channels set
            transaction.SetAddAsync("channels:active", channel);

            // Add metadata for diagnostics
            transaction.HashSetAsync($"message:{channel}:{messageId}:meta", new HashEntry[]
            {
                new("priority", (int)priority), new("timestamp", metadata.Timestamp.Ticks),
                new("type", metadata.TypeName)
            });

            // Execute all commands
            await transaction.ExecuteAsync();

            // Publish notification to subscribers (real-time notification)
            await _subscriber.PublishAsync(channel, messageId);

            // Update success metrics
            _circuitBreaker.OnSuccess();
            _lastMessageTimes[channel] = DateTime.UtcNow;

            // Notify local subscribers
            NotifySubscribers(channel, messageId);

            // Fire event
            MessagePublished?.Invoke(this, new MessagePublishedEventArgs(channel, messageId, priority));
          }
          catch (Exception ex)
          {
            _circuitBreaker.OnFailure();

            // Fall back to local persistence if available
            if (_persistentStorage != null)
            {
              await PersistMessageAsync(channel, serialized, metadata);
            }

            throw new GhostException("Failed to publish message to Redis", ex, ErrorCode.StorageConnectionFailed);
          }
        } else
        {
          // Circuit breaker is open, fall back to local persistence
          if (_persistentStorage != null)
          {
            await PersistMessageAsync(channel, serialized, metadata);
          } else
          {
            throw new GhostException("Redis connection unavailable and no fallback storage", ErrorCode.StorageConnectionFailed);
          }
        }
      }
      catch (Exception ex)
      {
        throw new GhostException("Failed to publish message", ex, ErrorCode.StorageConnectionFailed);
      }
    }

    /// <summary>
    /// Store a message in persistent storage for later delivery
    /// </summary>
    private async Task PersistMessageAsync(string channel, byte[] serializedMessage, MessageMetadata metadata)
    {
      if (_persistentStorage == null) return;

      try
      {
        var persistedMessage = new PersistedMessage
        {
            Id = metadata.Id,
            Channel = channel,
            Data = serializedMessage,
            Metadata = metadata,
            CreatedAt = DateTime.UtcNow
        };

        await _persistentStorage.StoreMessageAsync(persistedMessage);
        G.LogInfo($"Message {metadata.Id} persisted for later delivery to channel {channel}");
      }
      catch (Exception ex)
      {
        G.LogError(ex, $"Failed to persist message {metadata.Id} to {channel}");
      }
    }

    /// <summary>
    /// Flush persisted messages to Redis when connection is restored
    /// </summary>
    private async void FlushPersistedMessagesAsync(object state)
    {
      if (_isDisposed || !_isAvailable || _persistentStorage == null) return;

      try
      {
        var messages = await _persistentStorage.GetPendingMessagesAsync(50); // Process 50 at a time
        if (messages.Count == 0) return;

        G.LogInfo($"Flushing {messages.Count} persisted messages to Redis");

        foreach (var message in messages)
        {
          try
          {
            // Use Redis transaction for atomic operations
            var transaction = _db.CreateTransaction();

            // Store the message with automatic expiry
            string messageKey = $"message:{message.Channel}:{message.Id}";
            var expiry = message.Metadata.Expiry - DateTime.UtcNow;
            if (expiry <= TimeSpan.Zero) continue; // Skip expired messages

            transaction.StringSetAsync(messageKey, message.Data, expiry);
            transaction.StringSetAsync($"channel:{message.Channel}:last", message.Id);
            transaction.SetAddAsync("channels:active", message.Channel);

            // Add metadata
            transaction.HashSetAsync($"message:{message.Channel}:{message.Id}:meta", new HashEntry[]
            {
                new("priority", (int)message.Metadata.Priority), new("timestamp", message.Metadata.Timestamp.Ticks),
                new("type", message.Metadata.TypeName), new("persisted", true)
            });

            // Execute all commands
            await transaction.ExecuteAsync();

            // Publish notification to subscribers
            await _subscriber.PublishAsync(message.Channel, message.Id);

            // Mark as processed
            await _persistentStorage.MarkMessageProcessedAsync(message.Id);

            // Notify local subscribers
            NotifySubscribers(message.Channel, message.Id);
          }
          catch (Exception ex)
          {
            G.LogWarn($"Failed to flush persisted message {message.Id}: {ex.Message}");
            // Don't remove from persistence to try again later
          }
        }
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error in persistence flush timer");
      }
    }

    /// <summary>
    /// Subscribe to messages on the specified channel or pattern
    /// </summary>
    public async IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
      if (_isDisposed) throw new ObjectDisposedException(nameof(RedisGhostBus));
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

      // For exact channel matches, also subscribe via Redis pub/sub
      if (!channelPattern.Contains('*'))
      {
        try
        {
          // Explicitly subscribe to Redis channel
          await _subscriber.SubscribeAsync(channelPattern, (redisChannel, value) =>
          {
            string messageId = value.ToString();
            channel.Writer.TryWrite((redisChannel, messageId));
          });

          G.LogInfo($"Subscribed to Redis channel {channelPattern}");
        }
        catch (Exception ex)
        {
          G.LogWarn($"Redis subscription failed for {channelPattern}: {ex.Message}");
          // Continue with local subscription only
        }
      } else
      {
        // For pattern subscriptions, we need to identify all matching channels
        try
        {
          var activeChannels = await _db.SetMembersAsync("channels:active");
          var matchingChannels = activeChannels
              .Select(ch => ch.ToString())
              .Where(ch => MatchesPattern(ch, channelPattern))
              .ToList();

          foreach (var matchingChannel in matchingChannels)
          {
            await _subscriber.SubscribeAsync(matchingChannel, (redisChannel, value) =>
            {
              string messageId = value.ToString();
              channel.Writer.TryWrite((redisChannel, messageId));
            });
          }

          G.LogInfo($"Pattern subscription {channelPattern} matched {matchingChannels.Count} channels");
        }
        catch (Exception ex)
        {
          G.LogWarn($"Redis pattern subscription failed for {channelPattern}: {ex.Message}");
          // Continue with local subscription only
        }
      }

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

              // Get the message from Redis or local cache
              string key = $"message:{topic}:{messageId}";
              byte[] data = null;


              // First try Redis
              if (_circuitBreaker.IsAllowed)
              {
                try
                {
                  RedisValue value = await _db.StringGetAsync(key);
                  if (value.HasValue)
                  {
                    data = (byte[])value;
                    _circuitBreaker.OnSuccess();
                  }
                }
                catch (Exception ex)
                {
                  G.LogWarn($"Error fetching message {messageId} from Redis: {ex.Message}");
                  _circuitBreaker.OnFailure();
                }
              }

              // Fallback to persistent storage
              if (data == null && _persistentStorage != null)
              {
                var persistedMessage = await _persistentStorage.GetMessageAsync(messageId);
                if (persistedMessage != null)
                {
                  data = persistedMessage.Data;
                  G.LogInfo($"Retrieved message {messageId} from persistent storage");
                }
              }

              if (data != null && data.Length > 0)
              {
                // Deserialize and yield the message using MemoryPack
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

        // Unsubscribe from Redis
        if (!channelPattern.Contains('*'))
        {
          try
          {
            await _subscriber.UnsubscribeAsync(channelPattern);
          }
          catch
          { /* Ignore unsubscribe errors during cleanup */
          }
        }

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
        if (_circuitBreaker.IsAllowed)
        {
          try
          {
            // Get active channels from Redis
            var activeChannelsRedis = await _db.SetMembersAsync("channels:active");
            var activeChannels = activeChannelsRedis.Select(c => c.ToString()).ToList();

            // Filter channels that match the pattern
            var matchingChannels = activeChannels.Where(ch => MatchesPattern(ch, channelPattern)).ToList();

            foreach (var channel in matchingChannels)
            {
              cancellationToken.ThrowIfCancellationRequested();

              // Get latest message ID for the channel
              var lastMessageId = await _db.StringGetAsync($"channel:{channel}:last");

              if (!lastMessageId.IsNullOrEmpty)
              {
                // Add to processing queue
                writer.TryWrite((channel, lastMessageId.ToString()));
              }
            }

            _circuitBreaker.OnSuccess();
          }
          catch (Exception ex)
          {
            G.LogWarn($"Error processing existing Redis messages for {channelPattern}: {ex.Message}");
            _circuitBreaker.OnFailure();
          }
        }

        // Also check persisted messages if available
        if (_persistentStorage != null)
        {
          try
          {
            var persistedMessages = await _persistentStorage.GetMessagesByChannelPatternAsync(channelPattern);
            foreach (var msg in persistedMessages)
            {
              cancellationToken.ThrowIfCancellationRequested();
              writer.TryWrite((msg.Channel, msg.Id));
            }
          }
          catch (Exception ex)
          {
            G.LogWarn($"Error processing persisted messages for {channelPattern}: {ex.Message}");
          }
        }
      }
      catch (OperationCanceledException)
      {
        // Normal cancellation, just exit
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
      if (_isDisposed) throw new ObjectDisposedException(nameof(RedisGhostBus));
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

        // Unsubscribe from Redis
        if (!channelPattern.Contains('*'))
        {
          try
          {
            await _subscriber.UnsubscribeAsync(channelPattern);
            G.LogInfo($"Unsubscribed from Redis channel {channelPattern}");
          }
          catch (Exception ex)
          {
            G.LogWarn($"Redis unsubscribe failed for {channelPattern}: {ex.Message}");
          }
        }
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
        // First check circuit breaker state
        if (!_circuitBreaker.IsAllowed)
        {
          // If circuit breaker is open, check if we have persistent storage as fallback
          return _persistentStorage != null;
        }

        // Verify Redis connection
        if (!_redis.IsConnected)
        {
          return false;
        }

        // Try to set and get a test value
        string testKey = $"ghost:bus:test:{Guid.NewGuid()}";
        string testValue = Guid.NewGuid().ToString();

        await _db.StringSetAsync(testKey, testValue, TimeSpan.FromSeconds(5));
        string result = await _db.StringGetAsync(testKey);

        _isAvailable = testValue == result;

        if (_isAvailable)
        {
          _circuitBreaker.OnSuccess();

          // Update connection state if needed
          if (_connectionState != ConnectionState.Connected)
          {
            await UpdateConnectionStateAsync(ConnectionState.Connected, null);
          }
        } else
        {
          _circuitBreaker.OnFailure();

          // Update connection state if needed
          if (_connectionState == ConnectionState.Connected)
          {
            await UpdateConnectionStateAsync(ConnectionState.Degraded, "Redis test value mismatch");
          }
        }

        return _isAvailable || _persistentStorage != null;
      }
      catch (Exception ex)
      {
        G.LogWarn($"Message bus availability check failed: {ex.Message}");
        _circuitBreaker.OnFailure();

        // Update connection state if needed
        if (_connectionState == ConnectionState.Connected)
        {
          await UpdateConnectionStateAsync(ConnectionState.Disconnected, ex.Message);
        }

        // Still available if we have persistent storage as fallback
        return _persistentStorage != null;
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
    /// Get diagnostic information about the bus
    /// </summary>
    public BusDiagnostics GetDiagnostics()
    {
      return new BusDiagnostics
      {
          IsConnected = _isAvailable,
          ConnectionState = _connectionState,
          CircuitBreakerState = _circuitBreaker.State,
          SubscriptionCount = _subscriptions.Count,
          RedisEndpoints = _redis.GetEndPoints().Select(e => e.ToString()).ToList(),
          LastMessageTimestamps = _lastMessageTimes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
          HasFallbackStorage = true,
          PendingPersistedMessages = (int)_persistentStorage?.GetPendingCount().GetAwaiter().GetResult()!
      };
    }

    /// <summary>
    /// Register a callback for a channel pattern
    /// </summary>
    private void RegisterCallback(string channelPattern, Action<string, string> callback)
    {
      _callbacks.AddOrUpdate(
          channelPattern,
          new List<Action<string, string>>
          {
              callback
          },
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

          // Clean up expired messages from persistent storage
          if (_persistentStorage != null)
          {
            await _persistentStorage.CleanupExpiredMessagesAsync();
          }
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
    public void Dispose()
    {
      DisposeAsync().GetAwaiter().GetResult();
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

        // Disconnect Redis event handlers
        _redis.ConnectionFailed -= OnRedisConnectionFailed;
        _redis.ConnectionRestored -= OnRedisConnectionRestored;

        // Cancel all subscriptions
        foreach (var (_, cts) in _subscriptions)
        {
          cts.Cancel();
          cts.Dispose();
        }

        _subscriptions.Clear();
        _callbacks.Clear();

        // Clean up timers
        await _cleanupTimer.DisposeAsync();
        await _connectionMonitorTimer.DisposeAsync();

        if (_persistenceFlushTimer != null)
        {
          await _persistenceFlushTimer.DisposeAsync();
        }

        // Clean up Redis connections
        _redis.Close();
        if (_redis is IDisposable disposable)
        {
          disposable.Dispose();
        }

        // Clean up lock
        _lock.Dispose();

        G.LogInfo("RedisGhostBus disposed");
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

  /// <summary>
  /// Connection state for the message bus
  /// </summary>
  public enum ConnectionState
  {
    /// <summary>
    /// Not connected
    /// </summary>
    Disconnected,

    /// <summary>
    /// Connected with degraded performance
    /// </summary>
    Degraded,

    /// <summary>
    /// Fully connected
    /// </summary>
    Connected
  }

  /// <summary>
  /// Message metadata for persistence and diagnostics
  /// </summary>
  public class MessageMetadata
  {
    /// <summary>
    /// Unique message ID
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Target channel
    /// </summary>
    public string Channel { get; set; }

    /// <summary>
    /// Message priority
    /// </summary>
    public MessagePriority Priority { get; set; }

    /// <summary>
    /// Message creation timestamp
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Message expiration time
    /// </summary>
    public DateTime Expiry { get; set; }

    /// <summary>
    /// Full type name of the message
    /// </summary>
    public string TypeName { get; set; }
  }

  /// <summary>
  /// Persisted message for offline operation
  /// </summary>
  public class PersistedMessage
  {
    /// <summary>
    /// Unique message ID
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Target channel
    /// </summary>
    public string Channel { get; set; }

    /// <summary>
    /// Serialized message data
    /// </summary>
    public byte[] Data { get; set; }

    /// <summary>
    /// Message metadata
    /// </summary>
    public MessageMetadata Metadata { get; set; }

    /// <summary>
    /// Time when the message was persisted
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Flag indicating if the message has been processed
    /// </summary>
    public bool Processed { get; set; }
  }

  /// <summary>
  /// Bus diagnostics information
  /// </summary>
  public class BusDiagnostics
  {
    /// <summary>
    /// Whether the bus is connected
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Current connection state
    /// </summary>
    public ConnectionState ConnectionState { get; set; }

    /// <summary>
    /// Circuit breaker state
    /// </summary>
    public CircuitBreakerState CircuitBreakerState { get; set; }

    /// <summary>
    /// Number of active subscriptions
    /// </summary>
    public int SubscriptionCount { get; set; }

    /// <summary>
    /// Redis endpoints being used
    /// </summary>
    public List<string?> RedisEndpoints { get; set; }

    /// <summary>
    /// Last message timestamps by channel
    /// </summary>
    public Dictionary<string, DateTime> LastMessageTimestamps { get; set; }

    /// <summary>
    /// Whether fallback persistent storage is available
    /// </summary>
    public bool HasFallbackStorage { get; set; }

    /// <summary>
    /// Number of pending messages in persistent storage
    /// </summary>
    public int PendingPersistedMessages { get; set; }
  }

  /// <summary>
  /// Connection state changed event arguments
  /// </summary>
  public class ConnectionStateChangedEventArgs : EventArgs
  {
    /// <summary>
    /// Previous connection state
    /// </summary>
    public ConnectionState OldState { get; }

    /// <summary>
    /// New connection state
    /// </summary>
    public ConnectionState NewState { get; }

    /// <summary>
    /// Reason for the state change
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// Create a new connection state changed event
    /// </summary>
    public ConnectionStateChangedEventArgs(ConnectionState oldState, ConnectionState newState, string reason)
    {
      OldState = oldState;
      NewState = newState;
      Reason = reason;
    }
  }

  /// <summary>
  /// Message published event arguments
  /// </summary>
  public class MessagePublishedEventArgs : EventArgs
  {
    /// <summary>
    /// Channel the message was published to
    /// </summary>
    public string Channel { get; }

    /// <summary>
    /// Message ID
    /// </summary>
    public string MessageId { get; }

    /// <summary>
    /// Message priority
    /// </summary>
    public MessagePriority Priority { get; }

    /// <summary>
    /// Create a new message published event
    /// </summary>
    public MessagePublishedEventArgs(string channel, string messageId, MessagePriority priority)
    {
      Channel = channel;
      MessageId = messageId;
      Priority = priority;
    }
  }

  /// <summary>
  /// Interface for persistent storage of messages
  /// </summary>
  public interface IPersistentStorage
  {
    /// <summary>
    /// Store a message for later delivery
    /// </summary>
    Task StoreMessageAsync(PersistedMessage message);

    /// <summary>
    /// Get a message by ID
    /// </summary>
    Task<PersistedMessage> GetMessageAsync(string messageId);

    /// <summary>
    /// Get messages for a channel pattern
    /// </summary>
    Task<List<PersistedMessage>> GetMessagesByChannelPatternAsync(string channelPattern);

    /// <summary>
    /// Get a batch of pending messages for processing
    /// </summary>
    Task<List<PersistedMessage>> GetPendingMessagesAsync(int batchSize);

    /// <summary>
    /// Mark a message as processed
    /// </summary>
    Task MarkMessageProcessedAsync(string messageId);

    /// <summary>
    /// Get the count of pending messages
    /// </summary>
    Task<int> GetPendingCount();

    /// <summary>
    /// Clean up expired messages
    /// </summary>
    Task CleanupExpiredMessagesAsync();
  }

  /// <summary>
  /// Circuit breaker implementation for fail-fast pattern
  /// </summary>
  public class CircuitBreaker
  {
    private readonly int _maxFailures;
    private readonly TimeSpan _resetTimeout;
    private int _failureCount;
    private DateTime _lastFailure;
    private DateTime _openUntil;

    public CircuitBreakerState State
    {
      get
      {
        if (_failureCount >= _maxFailures)
        {
          return DateTime.UtcNow >= _openUntil
              ? CircuitBreakerState.HalfOpen
              : CircuitBreakerState.Open;
        }
        return CircuitBreakerState.Closed;
      }
    }

    public bool IsAllowed => State != CircuitBreakerState.Open;

    public CircuitBreaker(int maxFailures, TimeSpan resetTimeout)
    {
      _maxFailures = maxFailures;
      _resetTimeout = resetTimeout;
      _failureCount = 0;
      _lastFailure = DateTime.MinValue;
      _openUntil = DateTime.MinValue;
    }

    public void OnSuccess()
    {
      if (State == CircuitBreakerState.HalfOpen)
      {
        // Reset after successful operation in half-open state
        _failureCount = 0;
        _openUntil = DateTime.MinValue;
      }
    }

    public void OnFailure()
    {
      _lastFailure = DateTime.UtcNow;

      if (State == CircuitBreakerState.HalfOpen)
      {
        // Failed in half-open state, reset timeout
        _openUntil = DateTime.UtcNow.Add(_resetTimeout);
      } else if (State == CircuitBreakerState.Closed)
      {
        // Increment failure count
        _failureCount++;

        // If threshold reached, open the circuit
        if (_failureCount >= _maxFailures)
        {
          _openUntil = DateTime.UtcNow.Add(_resetTimeout);
        }
      }
    }

    public void Reset()
    {
      _failureCount = 0;
      _openUntil = DateTime.MinValue;
    }
  }

  /// <summary>
  /// Circuit breaker state
  /// </summary>
  public enum CircuitBreakerState
  {
    /// <summary>
    /// Circuit is closed and operating normally
    /// </summary>
    Closed,

    /// <summary>
    /// Circuit is open and rejecting requests
    /// </summary>
    Open,

    /// <summary>
    /// Circuit is allowing a test request
    /// </summary>
    HalfOpen
  }
}
