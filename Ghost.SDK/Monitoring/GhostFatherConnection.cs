using Ghost;
using Ghost.Storage;
using MemoryPack;
using System.Diagnostics;
using System.Threading.Channels;
using static System.TimeSpan;

namespace Ghost;

public class GhostFatherConnection : IAsyncDisposable
{
  private readonly IGhostBus _bus;
  private readonly string _id;
  private readonly ProcessInfo _processInfo;
  private readonly Timer _heartbeatTimer;
  private readonly Timer _metricsTimer;
  private readonly Timer _reconnectTimer;
  private readonly Timer _diagnosticsTimer;
  private readonly SemaphoreSlim _lock = new(1, 1);
  private readonly Channel<MessageEnvelope> _messageQueue;
  private readonly int _maxQueueSize = 1000;
  private readonly int _maxReconnectAttempts = 5;
  private readonly TimeSpan _initialReconnectDelay = FromSeconds(5);
  private readonly IConnectionDiagnostics _diagnostics;

  // Connection state
  private bool _isDisposed;
  private bool _isConnected;
  private int _reconnectAttempts;
  private DateTime _connectionEstablishedTime;
  private DateTime _lastHeartbeatSentTime;
  private DateTime _lastMessageReceivedTime;
  private DateTime _lastMetricsSentTime;

  // Fallback mechanisms
  private readonly IDirectCommunication? _directComm;
  private bool _usingFallbackComm;

  // Flag to indicate if this connection is for the daemon itself
  private readonly bool _isDaemonSelf;

  /// <summary>
  /// Unique identifier for this connection
  /// </summary>
  public string Id => _id;

  /// <summary>
  /// Process metadata associated with this connection
  /// </summary>
  public ProcessInfo ProcessInfo => _processInfo;

  /// <summary>
  /// Whether the connection is currently active
  /// </summary>
  public bool IsConnected => _isConnected;

  /// <summary>
  /// Last error encountered during communication
  /// </summary>
  public string LastError { get; private set; }

  /// <summary>
  /// Connection statistics
  /// </summary>
  public ConnectionStatistics Statistics { get; } = new ConnectionStatistics();

  /// <summary>
  /// Event raised when connection status changes
  /// </summary>
  public event EventHandler<ConnectionStatusEventArgs> ConnectionStatusChanged;

  public record ConnectionStatusEventArgs(bool IsConnected, string ErrorMessage);

  /// <summary>
  /// Event raised when a diagnostic result is available
  /// </summary>
  public event EventHandler<ConnectionDiagnosticsEventArgs> DiagnosticsCompleted;

  /// <summary>
  /// Creates a new connection to GhostFather with the specified metadata
  /// </summary>
  /// <param name="bus">Message bus for communication</param>
  /// <param name="processInfo">Metadata describing the current process</param>
  /// <param name="directComm">Optional direct communication fallback</param>
  /// <param name="diagnostics">Optional connection diagnostics provider</param>
  public GhostFatherConnection(
      IGhostBus bus,
      ProcessInfo processInfo,
      IDirectCommunication? directComm = null,
      IConnectionDiagnostics diagnostics = null)
  {
    if (bus == null) throw new ArgumentNullException(nameof(bus));
    if (processInfo == null) throw new ArgumentNullException(nameof(processInfo));

    _id = $"app-{Guid.NewGuid()}";
    _processInfo = processInfo;
    _bus = bus;
    _directComm = directComm;
    _diagnostics = diagnostics;

    // Check if this connection is for the daemon itself
    _isDaemonSelf = processInfo.Metadata.Name == "Ghost Father Daemon" ||
                    (processInfo.Metadata.Configuration != null &&
                     processInfo.Metadata.Configuration.TryGetValue("AppType", out var appType) &&
                     appType == "daemon");

    // Set up message queue with bounded capacity
    _messageQueue = Channel.CreateBounded<MessageEnvelope>(new BoundedChannelOptions(_maxQueueSize)
    {
        FullMode = BoundedChannelFullMode.DropOldest, // Drop oldest messages when full
        SingleReader = true,
        SingleWriter = false
    });

    // Create timers but don't start them yet
    _heartbeatTimer = new Timer(SendHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
    _metricsTimer = new Timer(SendMetrics, null, Timeout.Infinite, Timeout.Infinite);
    _reconnectTimer = new Timer(TryReconnect, null, Timeout.Infinite, Timeout.Infinite);
    _diagnosticsTimer = new Timer(RunDiagnosticsAsync, null, Timeout.Infinite, Timeout.Infinite);

    // Start message processor
    _ = ProcessMessageQueueAsync();

    G.LogDebug($"Created enhanced GhostFather connection with ID: {_id}");
  }

  /// <summary>
  /// Starts reporting metrics and heartbeats to GhostFather
  /// </summary>
  public async Task StartReporting()
  {
    if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

    await _lock.WaitAsync();
    try
    {
      // If this is the daemon itself, consider the connection already established
      if (_isDaemonSelf)
      {
        _isConnected = true;
        _reconnectAttempts = 0;
        _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _connectionEstablishedTime = DateTime.UtcNow;

        // Skip the standard connection check for the daemon
        G.LogInfo("Daemon is self-connected, no external connection needed");

        // Still start timers for metrics and heartbeats
        _heartbeatTimer.Change(Zero, FromSeconds(30));
        _metricsTimer.Change(Zero, FromSeconds(5));
        _diagnosticsTimer.Change(Zero, FromMinutes(5));

        OnConnectionStatusChanged(true, null);
        return;
      }

      // Regular connection check for non-daemon processes
      if (!await CheckConnectionAsync())
      {
        _isConnected = false;
        G.LogWarn("Could not connect to GhostFather daemon, will try reconnecting...");

        // Try to start diagnostics
        RunDiagnosticsAsync(null);

        // Schedule reconnect with exponential backoff
        ScheduleReconnect();
        return;
      }

      _isConnected = true;
      _reconnectAttempts = 0;
      _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);
      _connectionEstablishedTime = DateTime.UtcNow;

      // Register with GhostFather
      await RegisterWithDaemonAsync();

      // Start sending heartbeats every 30 seconds
      _heartbeatTimer.Change(Zero, FromSeconds(30));

      // Start sending metrics every 5 seconds
      _metricsTimer.Change(Zero, FromSeconds(5));

      // Start diagnostics timer (every 5 minutes)
      _diagnosticsTimer.Change(Zero, FromMinutes(5));

      G.LogInfo("Started reporting to GhostFather");
      OnConnectionStatusChanged(true, null);

      // Update statistics
      Statistics.LastConnectionTime = DateTime.UtcNow;
      Statistics.TotalConnections++;
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      _isConnected = false;
      G.LogError(ex, "Failed to start reporting to GhostFather");

      // Start reconnection attempts (unless this is the daemon)
      if (!_isDaemonSelf)
      {
        ScheduleReconnect();
      }

      OnConnectionStatusChanged(false, ex.Message);

      // Update statistics
      Statistics.LastErrorTime = DateTime.UtcNow;
      Statistics.TotalErrors++;
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Schedule reconnection with exponential backoff
  /// </summary>
  private void ScheduleReconnect()
  {
    // Calculate exponential backoff with jitter
    var baseDelay = _initialReconnectDelay.TotalMilliseconds * Math.Pow(1.5, _reconnectAttempts);
    var jitter = new Random().NextDouble() * 0.3 + 0.85; // 85-115% of base delay
    var delayMs = (int)(baseDelay * jitter);

    // Cap maximum delay at 2 minutes
    delayMs = Math.Min(delayMs, 120000);

    _reconnectTimer.Change(delayMs, Timeout.Infinite);

    G.LogInfo($"Will attempt reconnection in {delayMs / 1000.0:F1} seconds (attempt {_reconnectAttempts + 1})");
  }

  /// <summary>
  /// Check if the daemon is reachable using multiple methods
  /// </summary>
  private async Task<bool> CheckConnectionAsync()
  {
    // If this is the daemon itself, always return true
    if (_isDaemonSelf)
    {
      return true;
    }

    // Try the primary bus-based ping first
    if (await _bus.IsAvailableAsync())
    {
      if (await TryBusPingAsync())
      {
        _usingFallbackComm = false;
        return true;
      }
    }

    // If bus ping fails and we have direct communication fallback, try that
    if (_directComm != null)
    {
      G.LogInfo("Attempting direct communication fallback");

      if (await _directComm.TestConnectionAsync())
      {
        _usingFallbackComm = true;
        G.LogInfo("Using direct communication fallback");
        return true;
      }
    }

    // If all connection attempts fail, check if daemon is running
    if (_diagnostics != null && await _diagnostics.IsDaemonProcessRunningAsync())
    {
      G.LogWarn("Daemon process is running but cannot be reached through any communication channel");
    }

    return false;
  }

  /// <summary>
  /// Try to ping the daemon using the bus
  /// </summary>
  private async Task<bool> TryBusPingAsync()
  {
    try
    {
      var pingCommand = new SystemCommand
      {
          CommandId = Guid.NewGuid().ToString(),
          CommandType = "ping",
          Parameters = new Dictionary<string, string>
          {
              ["responseChannel"] = $"ghost:responses:{_id}:{Guid.NewGuid()}"
          }
      };

      var responseChannel = pingCommand.Parameters["responseChannel"];
      G.LogInfo("Sending ping to response channel: " + responseChannel);

      // Publish with high priority to ensure delivery
      if (_bus is RedisGhostBus redisBus)
      {
        await redisBus.PublishWithPriorityAsync("ghost:commands", pingCommand, MessagePriority.High);
      } else
      {
        await _bus.PublishAsync("ghost:commands", pingCommand);
      }

      // Wait for response with timeout
      using var cts = new CancellationTokenSource(FromSeconds(5));

      try
      {
        await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
        {
          if (response.CommandId == pingCommand.CommandId && response.Success)
          {
            return true;
          }
        }
      }
      catch (OperationCanceledException)
      {
        // Timeout occurred
        return false;
      }

      return false;
    }
    catch (Exception ex)
    {
      G.LogWarn($"Error checking daemon connection: {ex.Message}");
      return false;
    }
  }

  /// <summary>
  /// Try to reconnect to the daemon
  /// </summary>
  private async void TryReconnect(object state)
  {
    // Skip reconnect attempts if this is the daemon itself
    if (_isDisposed || _isConnected || _isDaemonSelf) return;

    await _lock.WaitAsync();
    try
    {
      if (_isDisposed || _isConnected) return;

      _reconnectAttempts++;
      G.LogInfo($"Attempting to reconnect to GhostFather daemon (attempt {_reconnectAttempts}/{_maxReconnectAttempts})...");
      G.LogDebug($"[Reconnect] Attempt {_reconnectAttempts}/{_maxReconnectAttempts}: ID={_id}");

      // Update statistics
      Statistics.TotalReconnectAttempts++;

      if (await CheckConnectionAsync())
      {
        _isConnected = true;
        _reconnectAttempts = 0;
        _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _connectionEstablishedTime = DateTime.UtcNow;

        // Re-register with daemon
        await RegisterWithDaemonAsync();

        // Restart timers
        _heartbeatTimer.Change(Zero, FromSeconds(30));
        _metricsTimer.Change(Zero, FromSeconds(5));
        _diagnosticsTimer.Change(Zero, FromMinutes(5));

        G.LogInfo("Reconnected to GhostFather daemon");
        G.LogDebug($"[Reconnect] Successfully reconnected: ID={_id}, ConnectionUptime={DateTime.UtcNow - _connectionEstablishedTime}");
        OnConnectionStatusChanged(true, null);

        // Update statistics
        Statistics.LastConnectionTime = DateTime.UtcNow;
        Statistics.TotalConnections++;
        Statistics.ConsecutiveFailures = 0;
      } else if (_reconnectAttempts >= _maxReconnectAttempts)
      {
        G.LogWarn($"Failed to reconnect to GhostFather daemon after {_maxReconnectAttempts} attempts");

        // Slow down reconnect attempts to reduce resource usage
        _reconnectTimer.Change((uint)FromMinutes(1).TotalMilliseconds, Timeout.Infinite);

        // Update statistics
        Statistics.ConsecutiveFailures++;

        // Try to run diagnostics
        RunDiagnosticsAsync(null);
      } else
      {
        // Schedule next reconnect with backoff
        ScheduleReconnect();

        // Update statistics
        Statistics.ConsecutiveFailures++;
      }
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      G.LogError(ex, "Error during reconnection attempt");

      // Still schedule another attempt
      ScheduleReconnect();

      // Update statistics
      Statistics.LastErrorTime = DateTime.UtcNow;
      Statistics.TotalErrors++;
      Statistics.ConsecutiveFailures++;
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Run diagnostics on the connection
  /// </summary>
  private async void RunDiagnosticsAsync(object state)
  {
    if (_isDisposed || _diagnostics == null) return;

    try
    {
      G.LogDebug($"[Diagnostics] Starting connection diagnostics: ID={_id}, IsConnected={_isConnected}");
      G.LogInfo("Running connection diagnostics");

      // Create diagnostic request
      var diagnosticRequest = new ConnectionDiagnosticRequest
      {
          ConnectionId = _id,
          Timestamp = DateTime.UtcNow,
          IsConnected = _isConnected,
          ReconnectAttempts = _reconnectAttempts,
          LastError = LastError,
          UsingFallback = _usingFallbackComm
      };

      // Run diagnostic tests
      var results = await _diagnostics.RunDiagnosticsAsync(diagnosticRequest);

      // Log detailed results
      G.LogInfo($"Diagnostics results: Redis available: {results.IsRedisAvailable}, " +
                $"Daemon running: {results.IsDaemonRunning}, Network OK: {results.IsNetworkOk}, " +
                $"Permissions OK: {results.HasRequiredPermissions}");

      G.LogDebug($"[Diagnostics] Results: Redis={results.IsRedisAvailable}, Daemon={results.IsDaemonRunning}, Network={results.IsNetworkOk}");

      // Notify listeners
      DiagnosticsCompleted?.Invoke(this, new ConnectionDiagnosticsEventArgs(results));

      // Take action based on diagnostics
      if (!results.IsDaemonRunning && results.CanAutoStartDaemon)
      {
        G.LogInfo("Daemon not running, attempting to auto-start");

        if (await _diagnostics.TryStartDaemonAsync())
        {
          G.LogInfo("Daemon auto-started successfully, initiating reconnect");
          _reconnectTimer.Change((uint)FromSeconds(3).TotalMilliseconds, Timeout.Infinite);
        }
      } else if (!results.IsRedisAvailable && results.CanUseFallback && _directComm != null)
      {
        G.LogInfo("Redis unavailable, switching to fallback communication");
        _usingFallbackComm = true;
      }
    }
    catch (Exception ex)
    {
      G.LogWarn($"Error running diagnostics: {ex.Message}");
    }
  }

  /// <summary>
  /// Registers this process with GhostFather
  /// </summary>
  private async Task RegisterWithDaemonAsync()
  {
    // Skip registration if this is the daemon itself - it registers directly
    if (_isDaemonSelf)
    {
      G.LogDebug("Skipping daemon self-registration through connection");
      return;
    }

    try
    {
      G.LogDebug($"[Registration] Starting process registration: ID={_id}, Name={_processInfo.Metadata.Name}");

      // Create registration message with process info
      var registration = new ProcessRegistration
      {
          Id = _processInfo.Id,
          Name = _processInfo.Metadata.Name,
          Type = _processInfo.Metadata.Type,
          Version = _processInfo.Metadata.Version,
          ExecutablePath = Process.GetCurrentProcess().MainModule?.FileName ?? "unknown",
          Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)),
          WorkingDirectory = Directory.GetCurrentDirectory(),
          Environment = _processInfo.Metadata.Environment,
          Configuration = _processInfo.Metadata.Configuration
      };

      if (_usingFallbackComm && _directComm != null)
      {
        // Use direct communication for registration
        await _directComm.RegisterProcessAsync(registration);
        G.LogInfo($"Registered with GhostFather using direct communication: {_processInfo.Metadata.Name} ({_id})");
      } else
      {
        // Serialize the registration using MemoryPack for better performance
        byte[] registrationBytes = MemoryPackSerializer.Serialize(registration);

        // Send a register command with serialized data
        var registerCommand = new SystemCommand
        {
            CommandId = Guid.NewGuid().ToString(),
            CommandType = "register",
            Timestamp = DateTime.UtcNow,
            Parameters = new Dictionary<string, string>
            {
                ["responseChannel"] = $"ghost:responses:{_id}:{Guid.NewGuid()}"
            },
            Data = Convert.ToBase64String(registrationBytes)
        };

        // Queue the command for sending with high priority
        await EnqueueMessageAsync(GhostChannels.Commands, registerCommand, MessagePriority.High);

        // Also send as a system event for backward compatibility
        var systemEvent = new SystemEvent
        {
            Type = "process.registered",
            ProcessId = _id,
            Data = registrationBytes,
            Timestamp = DateTime.UtcNow
        };

        // Queue the event for sending
        await EnqueueMessageAsync(GhostChannels.Events, systemEvent, MessagePriority.High);

        await PublishProcessEventAsync("process_started", new
        {
            ProcessInfo = _processInfo,
            ConnectionId = _id,
            RegistrationTime = DateTime.UtcNow
        });

        G.LogInfo($"Registered with GhostFather as {_processInfo.Metadata.Name} ({_id})");
        G.LogDebug($"[Registration] Successfully registered with GhostFather: ID={_id}, Using fallback={_usingFallbackComm}");
      }

      // Update statistics
      Statistics.LastRegistrationTime = DateTime.UtcNow;
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      G.LogError(ex, "Failed to register with GhostFather");
      throw;
    }
  }

  /// <summary>
  /// Process the message queue asynchronously
  /// </summary>
  private async Task ProcessMessageQueueAsync()
  {
    try
    {
      while (!_isDisposed)
      {
        try
        {
          // Wait for a message to be available
          if (await _messageQueue.Reader.WaitToReadAsync())
          {
            while (_messageQueue.Reader.TryRead(out var message))
            {
              if (_isDisposed) break;

              try
              {
                G.LogDebug($"[MessageQueue] Processing message: channel={message.Channel}, type={message.MessageType}, attempt={message.RetryCount + 1}/{message.MaxRetries}");

                // Try to send the message
                if (!_isConnected && !_isDaemonSelf)
                {
                  // For non-critical messages, re-queue if we're not connected
                  if (message.Priority < MessagePriority.High)
                  {
                    // Only requeue if we haven't exceeded the retry count
                    if (message.RetryCount < message.MaxRetries)
                    {
                      message.RetryCount++;
                      await _messageQueue.Writer.WriteAsync(message);

                      // Update statistics
                      Statistics.TotalMessagesRequeued++;
                    } else
                    {
                      G.LogWarn($"Dropping message to {message.Channel} after {message.RetryCount} retries");

                      // Update statistics
                      Statistics.TotalMessagesDropped++;
                    }

                    await Task.Delay(1000); // Wait a bit before rechecking connection
                    break; // Break out of the inner loop to wait for connection
                  }
                }

                // Handle different communication methods
                if (_usingFallbackComm && _directComm != null)
                {
                  // Use direct communication
                  await ProcessMessageWithDirectComm(message);
                } else
                {
                  // Use regular bus
                  await ProcessMessageWithBus(message);
                }

                // Update statistics
                Statistics.TotalMessagesSent++;
                G.LogDebug($"[MessageQueue] Successfully processed message: channel={message.Channel}, type={message.MessageType}");
              }
              catch (Exception ex)
              {
                LastError = ex.Message;
                G.LogWarn($"Error processing message: {ex.Message}");

                // Update statistics
                Statistics.LastErrorTime = DateTime.UtcNow;
                Statistics.TotalErrors++;

                // If the error might be connection-related, mark as disconnected
                if (!_isDisposed && _isConnected && !_isDaemonSelf)
                {
                  await _lock.WaitAsync();
                  try
                  {
                    _isConnected = false;
                    OnConnectionStatusChanged(false, ex.Message);

                    // Handle critical messages - requeue them
                    if (message.Priority >= MessagePriority.High && message.RetryCount < message.MaxRetries)
                    {
                      message.RetryCount++;
                      await _messageQueue.Writer.WriteAsync(message);

                      // Update statistics
                      Statistics.TotalMessagesRequeued++;
                    }

                    // Start reconnection attempts
                    ScheduleReconnect();
                  }
                  finally
                  {
                    _lock.Release();
                  }
                }
              }
            }
          }
        }
        catch (OperationCanceledException)
        {
          // Normal cancellation
          break;
        }
        catch (Exception ex)
        {
          G.LogError(ex, "Error in message queue processor");
          await Task.Delay(1000); // Wait a bit before continuing
        }
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Fatal error in message queue processor");
    }
  }

  /// <summary>
  /// Process a message using direct communication
  /// </summary>
  private async Task ProcessMessageWithDirectComm(MessageEnvelope message)
  {
    if (_directComm == null)
      throw new InvalidOperationException("Direct communication is not available");

    switch (message.MessageType)
    {
      case MessageType.SystemEvent:
        await _directComm.SendEventAsync((SystemEvent)message.Message);
        break;

      case MessageType.SystemCommand:
        await _directComm.SendCommandAsync((SystemCommand)message.Message);
        break;

      case MessageType.Heartbeat:
        await _directComm.SendHeartbeatAsync((HeartbeatMessage)message.Message);
        break;

      case MessageType.HealthStatus:
        await _directComm.SendHealthStatusAsync((HealthStatusMessage)message.Message);
        break;

      case MessageType.Metrics:
        await _directComm.SendMetricsAsync((ProcessMetrics)message.Message);
        break;

      default:
        throw new NotSupportedException($"Message type {message.MessageType} not supported for direct communication");
    }
  }

  /// <summary>
  /// Process a message using the bus
  /// </summary>
  private async Task ProcessMessageWithBus(MessageEnvelope message)
  {
    // Process message based on type
    switch (message.MessageType)
    {
      case MessageType.SystemEvent:
        await PublishToBusWithPriority(message.Channel, message.Message, message.Priority);
        break;

      case MessageType.SystemCommand:
        await PublishToBusWithPriority(message.Channel, message.Message, message.Priority);
        break;

      case MessageType.Heartbeat:
        await PublishToBusWithPriority(message.Channel, message.Message, message.Priority);
        break;

      case MessageType.HealthStatus:
        await PublishToBusWithPriority(message.Channel, message.Message, message.Priority);
        break;

      case MessageType.Metrics:
        await PublishToBusWithPriority(message.Channel, message.Message, message.Priority);
        break;

      default:
        await PublishToBusWithPriority(message.Channel, message.Message, message.Priority);
        break;
    }
  }

  /// <summary>
  /// Publish to the bus with priority if available
  /// </summary>
  private async Task PublishToBusWithPriority(string channel, object message, MessagePriority priority)
  {
    if (_bus is RedisGhostBus redisBus)
    {
      await redisBus.PublishWithPriorityAsync(channel, message, priority);
    } else
    {
      await _bus.PublishAsync(channel, message);
    }
  }

  /// <summary>
  /// Enqueue a message for processing
  /// </summary>
  private async Task EnqueueMessageAsync<T>(string channel, T message, MessagePriority priority = MessagePriority.Normal)
  {
    // Create an envelope with metadata
    var envelope = new MessageEnvelope
    {
        Channel = channel,
        Message = message,
        MessageType = GetMessageType(message),
        Priority = priority,
        Timestamp = DateTime.UtcNow,
        RetryCount = 0,
        MaxRetries = GetMaxRetries(priority)
    };

    // Add to the message queue
    if (!await _messageQueue.Writer.WaitToWriteAsync())
    {
      // Queue is full or closed
      G.LogWarn($"Message queue full or closed, dropping message to {channel}");

      // Update statistics
      Statistics.TotalMessagesDropped++;
      return;
    }

    await _messageQueue.Writer.WriteAsync(envelope);
  }

  /// <summary>
  /// Get the message type from the message object
  /// </summary>
  private MessageType GetMessageType(object message)
  {
    if (message is SystemEvent) return MessageType.SystemEvent;
    if (message is SystemCommand) return MessageType.SystemCommand;
    if (message is HeartbeatMessage) return MessageType.Heartbeat;
    if (message is HealthStatusMessage) return MessageType.HealthStatus;
    if (message is ProcessMetrics) return MessageType.Metrics;
    return MessageType.Generic;
  }

  /// <summary>
  /// Get the maximum retries based on priority
  /// </summary>
  private int GetMaxRetries(MessagePriority priority)
  {
    return priority switch
    {
        MessagePriority.Low => 2,
        MessagePriority.Normal => 5,
        MessagePriority.High => 10,
        MessagePriority.Critical => 20,
        _ => 5
    };
  }

  /// <summary>
  /// Sends a heartbeat message to indicate the process is alive
  /// </summary>
  private async void SendHeartbeat(object state)
  {
    if (_isDisposed) return;

    try
    {
      G.LogDebug($"[Heartbeat] Sending heartbeat: ID={_id}, Status=Running");
      // Create a serializable heartbeat message
      var heartbeat = new HeartbeatMessage
      {
          Id = _id,
          Status = "Running",
          Timestamp = DateTime.UtcNow,
          AppType = _processInfo?.Metadata.Configuration != null
                    && _processInfo.Metadata.Configuration.TryGetValue("AppType", out var appType)
              ? appType : "unknown"
      };

      // Update last heartbeat time
      _lastHeartbeatSentTime = DateTime.UtcNow;

      // Add to the message queue for processing
      await EnqueueMessageAsync(
          string.Format(GhostChannels.Health, _id),
          heartbeat,
          MessagePriority.Normal);

      // Update statistics
      Statistics.TotalHeartbeatsSent++;
    }
    catch (Exception ex)
    {
      G.LogWarn($"Error queuing heartbeat: {ex.Message}");

      // Update statistics
      Statistics.LastErrorTime = DateTime.UtcNow;
      Statistics.TotalErrors++;
    }
  }

  /// <summary>
  /// Sends current process metrics to GhostFather
  /// </summary>
  private async void SendMetrics(object state)
  {
    if (_isDisposed) return;

    try
    {
      var process = Process.GetCurrentProcess();
      process.Refresh();

      G.LogDebug($"[Metrics] Collecting process metrics: ID={_id}, Memory={process.WorkingSet64 / 1024 / 1024}MB, Threads={process.Threads.Count}");

      var metrics = new ProcessMetrics(
          ProcessId: _id,
          CpuPercentage: CalculateCpuUsage(process),
          MemoryBytes: process.WorkingSet64,
          ThreadCount: process.Threads.Count,
          HandleCount: process.HandleCount,
          GcTotalMemory: GC.GetTotalMemory(false),
          Gen0Collections: GC.CollectionCount(0),
          Gen1Collections: GC.CollectionCount(1),
          Gen2Collections: GC.CollectionCount(2),
          Timestamp: DateTime.UtcNow
      );

      // Update last metrics time
      _lastMetricsSentTime = DateTime.UtcNow;

      // Add to the message queue for processing
      await EnqueueMessageAsync(
          string.Format(GhostChannels.Metrics, _id),
          metrics,
          MessagePriority.Low);

      // Update statistics
      Statistics.TotalMetricsSent++;
    }
    catch (Exception ex)
    {
      G.LogWarn($"Error queuing metrics: {ex.Message}");

      // Update statistics
      Statistics.LastErrorTime = DateTime.UtcNow;
      Statistics.TotalErrors++;
    }
  }

  // CPU usage tracking for more accurate metrics
  private DateTime _lastCpuCheck = DateTime.UtcNow;
  private TimeSpan _lastTotalProcessorTime = Zero;

  /// <summary>
  /// Calculate the CPU usage percentage
  /// </summary>
  private double CalculateCpuUsage(Process process)
  {
    try
    {
      var currentTime = DateTime.UtcNow;
      var currentTotalProcessorTime = process.TotalProcessorTime;

      if (_lastTotalProcessorTime == Zero)
      {
        _lastTotalProcessorTime = currentTotalProcessorTime;
        _lastCpuCheck = currentTime;
        return 0;
      }

      var elapsedTime = currentTime - _lastCpuCheck;
      if (elapsedTime.TotalMilliseconds < 100) return 0; // Avoid division by very small values

      var elapsedCpu = currentTotalProcessorTime - _lastTotalProcessorTime;
      var cpuUsage = 100.0 * elapsedCpu.TotalMilliseconds / (Environment.ProcessorCount * elapsedTime.TotalMilliseconds);

      _lastCpuCheck = currentTime;
      _lastTotalProcessorTime = currentTotalProcessorTime;

      return Math.Min(100, Math.Max(0, cpuUsage)); // Clamp between 0-100
    }
    catch
    {
      return 0;
    }
  }

  /// <summary>
  /// Manually reports current process metrics
  /// </summary>
  public async Task ReportMetricsAsync()
  {
    if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

    try
    {
      var process = Process.GetCurrentProcess();
      process.Refresh();

      var metrics = new ProcessMetrics(
          ProcessId: _id,
          CpuPercentage: CalculateCpuUsage(process),
          MemoryBytes: process.WorkingSet64,
          ThreadCount: process.Threads.Count,
          HandleCount: process.HandleCount,
          GcTotalMemory: GC.GetTotalMemory(false),
          Gen0Collections: GC.CollectionCount(0),
          Gen1Collections: GC.CollectionCount(1),
          Gen2Collections: GC.CollectionCount(2),
          Timestamp: DateTime.UtcNow
      );

      // Add to the message queue for processing
      await EnqueueMessageAsync(
          string.Format(GhostChannels.Metrics, _id),
          metrics,
          MessagePriority.Normal);

      // Update statistics
      Statistics.TotalMetricsSent++;
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      G.LogError(ex, "Failed to report metrics");

      // Update statistics
      Statistics.LastErrorTime = DateTime.UtcNow;
      Statistics.TotalErrors++;

      throw;
    }
  }

  /// <summary>
  /// Reports health status to GhostFather
  /// </summary>
  /// <param name="status">Current status (e.g. "Running", "Error", etc.)</param>
  /// <param name="message">Detailed status message</param>
  public async Task ReportHealthAsync(string status, string message)
  {
    if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));
    if (string.IsNullOrEmpty(status)) throw new ArgumentNullException(nameof(status));

    try
    {
      // Create a serializable health status message
      var healthInfo = new HealthStatusMessage
      {
          Id = _id,
          Status = status,
          Message = message ?? string.Empty,
          AppType = _processInfo.Metadata.Configuration != null && _processInfo.Metadata.Configuration.TryGetValue("AppType", out var appType) ? appType : "unknown",
          Timestamp = DateTime.UtcNow
      };

      // Determine priority based on status
      var priority = status.ToLower() switch
      {
          "error" => MessagePriority.High,
          "crashed" => MessagePriority.High,
          "critical" => MessagePriority.Critical,
          _ => MessagePriority.Normal
      };

      // Add to the message queue for processing
      await EnqueueMessageAsync(
          string.Format(GhostChannels.Health, _id),
          healthInfo,
          priority);

      G.LogDebug($"Queued health status report: {status}");

      // Update statistics
      Statistics.TotalHealthReportsSent++;
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      G.LogError(ex, "Failed to report health status");

      // Update statistics
      Statistics.LastErrorTime = DateTime.UtcNow;
      Statistics.TotalErrors++;

      throw;
    }
  }

  /// <summary>
  /// Sends a command to the GhostFather daemon
  /// </summary>
  /// <param name="commandType">Type of command to send</param>
  /// <param name="parameters">Command parameters</param>
  /// <param name="targetProcessId">Optional target process ID</param>
  /// <param name="data">Optional serialized data</param>
  /// <returns>The command response, or null if the command timed out</returns>
  public async Task<CommandResponse> SendCommandAsync(string commandType, Dictionary<string, string> parameters = null, string targetProcessId = null, object data = null)
  {
    if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));
    if (string.IsNullOrEmpty(commandType)) throw new ArgumentNullException(nameof(commandType));

    try
    {
      var commandId = Guid.NewGuid().ToString();
      var responseChannel = $"ghost:responses:{_id}:{Guid.NewGuid()}";

      var command = new SystemCommand
      {
          CommandId = commandId,
          CommandType = commandType,
          TargetProcessId = targetProcessId ?? string.Empty,
          Timestamp = DateTime.UtcNow,
          Parameters = parameters ?? new Dictionary<string, string>()
      };

      // Add response channel parameter
      command.Parameters["responseChannel"] = responseChannel;

      // Add data if provided
      if (data != null)
      {
        byte[] serializedData = MemoryPackSerializer.Serialize(data);
        command.Data = Convert.ToBase64String(serializedData);
      }

      // Determine priority based on command type
      var priority = commandType.ToLower() switch
      {
          "ping" => MessagePriority.High,
          "register" => MessagePriority.High,
          "stop" => MessagePriority.High,
          _ => MessagePriority.Normal
      };

      // Use direct communication if in fallback mode
      if (_usingFallbackComm && _directComm != null)
      {
        var response = await _directComm.SendCommandWithResponseAsync(command);
        return response;
      } else
      {
        // Queue the command for sending
        await EnqueueMessageAsync(GhostChannels.Commands, command, priority);

        // Update statistics
        Statistics.TotalCommandsSent++;

        // Wait for response with timeout
        using var cts = new CancellationTokenSource(FromSeconds(30));

        try
        {
          await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
          {
            if (response.CommandId == commandId)
            {
              // Update statistics
              Statistics.TotalCommandResponsesReceived++;
              _lastMessageReceivedTime = DateTime.UtcNow;

              return response;
            }
          }
        }
        catch (OperationCanceledException)
        {
          // Timeout occurred
          G.LogWarn($"Command {commandType} timed out");

          // Update statistics
          Statistics.TotalCommandTimeouts++;

          return new CommandResponse
          {
              CommandId = commandId,
              Success = false,
              Error = "Command timed out",
              Timestamp = DateTime.UtcNow
          };
        }

        return null;
      }
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      G.LogError(ex, $"Error sending command {commandType}");

      // Update statistics
      Statistics.LastErrorTime = DateTime.UtcNow;
      Statistics.TotalErrors++;

      throw;
    }
  }

  /// <summary>
  /// Get detailed connection status
  /// </summary>
  public ConnectionStatus GetConnectionStatus()
  {
    return new ConnectionStatus
    {
        Id = _id,
        IsConnected = _isConnected,
        UsingFallback = _usingFallbackComm,
        LastError = LastError,
        ReconnectAttempts = _reconnectAttempts,
        ConnectionUptime = _isConnected ? DateTime.UtcNow - _connectionEstablishedTime : Zero,
        QueuedMessageCount = _messageQueue.Reader.Count,
        LastMessageReceived = _lastMessageReceivedTime,
        LastHeartbeatSent = _lastHeartbeatSentTime,
        LastMetricsSent = _lastMetricsSentTime,
        Statistics = Statistics
    };
  }

  /// <summary>
  /// Notifies when connection status changes
  /// </summary>
  protected virtual void OnConnectionStatusChanged(bool isConnected, string error)
  {
    ConnectionStatusChanged?.Invoke(this, new ConnectionStatusEventArgs(isConnected, error));
  }

  /// <summary>
  /// Disposes the connection and reports process stopped
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    if (_isDisposed) return;

    await _lock.WaitAsync();
    try
    {
      if (_isDisposed) return;
      _isDisposed = true;

      // Stop timers if they exist
      _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
      _metricsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
      _reconnectTimer?.Change(Timeout.Infinite, Timeout.Infinite);
      _diagnosticsTimer?.Change(Timeout.Infinite, Timeout.Infinite);

      // Report process stopped if bus exists (and not for daemon itself)
      if (_isConnected && !_isDaemonSelf)
      {
        var systemEvent = new SystemEvent
        {
            Type = "process.stopped",
            ProcessId = _id,
            Timestamp = DateTime.UtcNow
        };

        // Try to send directly, don't queue since we're shutting down
        if (_usingFallbackComm && _directComm != null)
        {
          try
          {
            await _directComm.SendEventAsync(systemEvent);
            G.LogInfo($"Reported process stopped via direct communication: {_id}");
          }
          catch (Exception ex)
          {
            G.LogWarn($"Error reporting process stopped via direct communication: {ex.Message}");
          }
        } else
        {
          try
          {
            await _bus.PublishAsync(GhostChannels.Events, systemEvent);
            G.LogInfo($"Reported process stopped: {_id}");
          }
          catch (Exception ex)
          {
            G.LogWarn($"Error reporting process stopped: {ex.Message}");
          }
        }
      }

      // Complete the message queue
      _messageQueue.Writer.TryComplete();

      // Clean up resources
      await Task.WhenAll(
          _heartbeatTimer.DisposeAsync().AsTask(),
          _metricsTimer.DisposeAsync().AsTask(),
          _reconnectTimer.DisposeAsync().AsTask(),
          _diagnosticsTimer.DisposeAsync().AsTask()
      );

      // Dispose direct communication if available
      if (_directComm is IAsyncDisposable disposableComm)
      {
        await disposableComm.DisposeAsync();
      }

      _lock.Dispose();

      G.LogInfo($"Disposed GhostFatherConnection: {_id}");
    }
    finally
    {
      if (!_isDisposed)
      {
        _lock.Release();
      }
    }
  }

  // Add this method to GhostFatherConnection.cs
  private async Task PublishProcessEventAsync(string eventType, object eventData)
  {
    try
    {
      // Create a properly formatted process event
      var processEvent = new
      {
          Id = _id,
          EventType = eventType,
          ProcessName = _processInfo.Metadata.Name,
          ProcessType = _processInfo.Metadata.Type,
          Status = eventType switch
          {
              "process_started" => "online",
              "process_stopped" => "stopped",
              "process_failed" => "errored",
              _ => "unknown"
          },
          Timestamp = DateTime.UtcNow,
          Data = eventData,
          // Add fields the monitor expects
          Mode = _processInfo.Metadata.Type,
          StartTime = DateTime.UtcNow,
          Restarts = 0,
          User = Environment.UserName
      };

      // Publish to the global events channel that the monitor listens to
      await _bus.PublishAsync("ghost:events", processEvent);

      // Also publish to process-specific channel
      await _bus.PublishAsync($"ghost:events:{_id}", processEvent);

      G.LogDebug($"Published process event: {eventType} for {_id}");
    }
    catch (Exception ex)
    {
      G.LogWarn($"Failed to publish process event: {ex.Message}");
    }
  }

  private Timer _reportingTimer;
  private bool _isReporting;

  public async Task StopReporting()
  {
    if (!_isReporting) return;

    try
    {
      _isReporting = false;

      // Publish process stopped event
      await PublishProcessEventAsync("process_stopped", _processInfo);

      _reportingTimer.Dispose();
      _reportingTimer = null;

      G.LogInfo("Stopped reporting to GhostFather");
    }
    catch (Exception ex)
    {
      G.LogError($"Failed to stop reporting: {ex.Message}");
    }

  }
}
/// <summary>
/// Standard channel names for Ghost communication
/// </summary>
public static class GhostChannels
{
  /// <summary>
  /// Channel for system commands
  /// </summary>
  public const string Commands = "ghost:commands";

  /// <summary>
  /// Channel for system events
  /// </summary>
  public const string Events = "ghost:events";

  /// <summary>
  /// Format string for health channels (format with process ID)
  /// </summary>
  public const string Health = "ghost:health:{0}";

  /// <summary>
  /// Format string for metrics channels (format with process ID)
  /// </summary>
  public const string Metrics = "ghost:metrics:{0}";

  /// <summary>
  /// Format string for response channels (format with response ID)
  /// </summary>
  public const string Responses = "ghost:responses:{0}";
}
/// <summary>
/// Types of messages that can be sent through the connection
/// </summary>
public enum MessageType
{
  Generic,
  SystemEvent,
  SystemCommand,
  Heartbeat,
  HealthStatus,
  Metrics
}
/// <summary>
/// Message envelope with metadata for prioritization and retry
/// </summary>
public class MessageEnvelope
{
  /// <summary>
  /// Target channel
  /// </summary>
  public string Channel { get; set; }

  /// <summary>
  /// The message object
  /// </summary>
  public object Message { get; set; }

  /// <summary>
  /// Message type for specialized handling
  /// </summary>
  public MessageType MessageType { get; set; }

  /// <summary>
  /// Message priority
  /// </summary>
  public MessagePriority Priority { get; set; }

  /// <summary>
  /// When the message was created
  /// </summary>
  public DateTime Timestamp { get; set; }

  /// <summary>
  /// Number of delivery attempts
  /// </summary>
  public int RetryCount { get; set; }

  /// <summary>
  /// Maximum number of retries
  /// </summary>
  public int MaxRetries { get; set; }
}
/// <summary>
/// Connection status for detailed diagnostics
/// </summary>
public class ConnectionStatus
{
  /// <summary>
  /// Connection ID
  /// </summary>
  public string Id { get; set; }

  /// <summary>
  /// Whether the connection is active
  /// </summary>
  public bool IsConnected { get; set; }

  /// <summary>
  /// Whether using fallback communication
  /// </summary>
  public bool UsingFallback { get; set; }

  /// <summary>
  /// Last error message
  /// </summary>
  public string LastError { get; set; }

  /// <summary>
  /// Current reconnect attempt count
  /// </summary>
  public int ReconnectAttempts { get; set; }

  /// <summary>
  /// How long the connection has been established
  /// </summary>
  public TimeSpan ConnectionUptime { get; set; }

  /// <summary>
  /// Number of messages waiting in the queue
  /// </summary>
  public int QueuedMessageCount { get; set; }

  /// <summary>
  /// When the last message was received
  /// </summary>
  public DateTime LastMessageReceived { get; set; }

  /// <summary>
  /// When the last heartbeat was sent
  /// </summary>
  public DateTime LastHeartbeatSent { get; set; }

  /// <summary>
  /// When the last metrics were sent
  /// </summary>
  public DateTime LastMetricsSent { get; set; }

  /// <summary>
  /// Detailed connection statistics
  /// </summary>
  public ConnectionStatistics Statistics { get; set; }
}
/// <summary>
/// Connection statistics for monitoring
/// </summary>
public class ConnectionStatistics
{
  /// <summary>
  /// Total number of connections established
  /// </summary>
  public int TotalConnections { get; set; }

  /// <summary>
  /// Total number of connection errors
  /// </summary>
  public int TotalErrors { get; set; }

  /// <summary>
  /// Total number of reconnection attempts
  /// </summary>
  public int TotalReconnectAttempts { get; set; }

  /// <summary>
  /// Total number of messages sent
  /// </summary>
  public int TotalMessagesSent { get; set; }

  /// <summary>
  /// Total number of messages dropped
  /// </summary>
  public int TotalMessagesDropped { get; set; }

  /// <summary>
  /// Total number of messages requeued
  /// </summary>
  public int TotalMessagesRequeued { get; set; }

  /// <summary>
  /// Total number of heartbeats sent
  /// </summary>
  public int TotalHeartbeatsSent { get; set; }

  /// <summary>
  /// Total number of metrics reports sent
  /// </summary>
  public int TotalMetricsSent { get; set; }

  /// <summary>
  /// Total number of health reports sent
  /// </summary>
  public int TotalHealthReportsSent { get; set; }

  /// <summary>
  /// Total number of commands sent
  /// </summary>
  public int TotalCommandsSent { get; set; }

  /// <summary>
  /// Total number of command responses received
  /// </summary>
  public int TotalCommandResponsesReceived { get; set; }

  /// <summary>
  /// Total number of command timeouts
  /// </summary>
  public int TotalCommandTimeouts { get; set; }

  /// <summary>
  /// Number of consecutive connection failures
  /// </summary>
  public int ConsecutiveFailures { get; set; }

  /// <summary>
  /// Last time a connection was established
  /// </summary>
  public DateTime LastConnectionTime { get; set; }

  /// <summary>
  /// Last time registration was completed
  /// </summary>
  public DateTime LastRegistrationTime { get; set; }

  /// <summary>
  /// Last time an error occurred
  /// </summary>
  public DateTime LastErrorTime { get; set; }
}
/// <summary>
/// Connection diagnostic results
/// </summary>
public class ConnectionDiagnosticResults
{
  /// <summary>
  /// Whether Redis is available
  /// </summary>
  public bool IsRedisAvailable { get; set; }

  /// <summary>
  /// Whether the daemon is running
  /// </summary>
  public bool IsDaemonRunning { get; set; }

  /// <summary>
  /// Whether the network is functioning properly
  /// </summary>
  public bool IsNetworkOk { get; set; }

  /// <summary>
  /// Whether the process has required permissions
  /// </summary>
  public bool HasRequiredPermissions { get; set; }

  /// <summary>
  /// Whether fallback communication is available
  /// </summary>
  public bool CanUseFallback { get; set; }

  /// <summary>
  /// Whether the daemon can be auto-started
  /// </summary>
  public bool CanAutoStartDaemon { get; set; }

  /// <summary>
  /// Detailed diagnostic message
  /// </summary>
  public string DiagnosticMessage { get; set; }

  /// <summary>
  /// Recommended actions to fix the issue
  /// </summary>
  public List<string> RecommendedActions { get; set; } = new List<string>();
}
/// <summary>
/// Connection diagnostic request
/// </summary>
public class ConnectionDiagnosticRequest
{
  /// <summary>
  /// Connection ID
  /// </summary>
  public string ConnectionId { get; set; }

  /// <summary>
  /// Current timestamp
  /// </summary>
  public DateTime Timestamp { get; set; }

  /// <summary>
  /// Whether currently connected
  /// </summary>
  public bool IsConnected { get; set; }

  /// <summary>
  /// Number of reconnect attempts
  /// </summary>
  public int ReconnectAttempts { get; set; }

  /// <summary>
  /// Last error message
  /// </summary>
  public string LastError { get; set; }

  /// <summary>
  /// Whether using fallback communication
  /// </summary>
  public bool UsingFallback { get; set; }
}
/// <summary>
/// Connection diagnostics event arguments
/// </summary>
public class ConnectionDiagnosticsEventArgs : EventArgs
{
  /// <summary>
  /// Diagnostic results
  /// </summary>
  public ConnectionDiagnosticResults Results { get; }

  /// <summary>
  /// Create new diagnostics event arguments
  /// </summary>
  public ConnectionDiagnosticsEventArgs(ConnectionDiagnosticResults results)
  {
    Results = results;
  }
}
/// <summary>
/// Interface for connection diagnostics
/// </summary>
public interface IConnectionDiagnostics
{
  /// <summary>
  /// Run diagnostics on the connection
  /// </summary>
  Task<ConnectionDiagnosticResults> RunDiagnosticsAsync(ConnectionDiagnosticRequest request);

  /// <summary>
  /// Check if the daemon process is running
  /// </summary>
  Task<bool> IsDaemonProcessRunningAsync();

  /// <summary>
  /// Try to start the daemon process
  /// </summary>
  Task<bool> TryStartDaemonAsync();
}
/// <summary>
/// Interface for direct communication fallback
/// </summary>
public interface IDirectCommunication
{
  /// <summary>
  /// Test the connection to the daemon
  /// </summary>
  Task<bool> TestConnectionAsync();

  /// <summary>
  /// Register a process with the daemon
  /// </summary>
  Task RegisterProcessAsync(ProcessRegistration registration);

  /// <summary>
  /// Send a system event to the daemon
  /// </summary>
  Task SendEventAsync(SystemEvent systemEvent);

  /// <summary>
  /// Send a command to the daemon
  /// </summary>
  Task SendCommandAsync(SystemCommand command);

  /// <summary>
  /// Send a command and wait for a response
  /// </summary>
  Task<CommandResponse> SendCommandWithResponseAsync(SystemCommand command);

  /// <summary>
  /// Send a heartbeat message
  /// </summary>
  Task SendHeartbeatAsync(HeartbeatMessage heartbeat);

  /// <summary>
  /// Send a health status update
  /// </summary>
  Task SendHealthStatusAsync(HealthStatusMessage healthStatus);

  /// <summary>
  /// Send metrics data
  /// </summary>
  Task SendMetricsAsync(ProcessMetrics metrics);
}
