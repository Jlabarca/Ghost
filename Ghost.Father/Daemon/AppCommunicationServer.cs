using Ghost.Core;
using Ghost.Core.Storage;
using MemoryPack;
using System.Collections.Concurrent;
namespace Ghost.Father.Daemon;

/// <summary>
/// Manages communication with ghost apps that connect to the daemon
/// </summary>
public class AppCommunicationServer
{
  private readonly IGhostBus _bus;
  private readonly HealthMonitor _healthMonitor;
  private readonly StateManager _stateManager;
  private readonly ConcurrentDictionary<string, AppConnectionInfo> _connections = new();
  private readonly SemaphoreSlim _lock = new(1, 1);
  private readonly CancellationTokenSource _cts = new();
  private readonly TimeSpan _connectionTimeout = TimeSpan.FromMinutes(2); // Consider app disconnected after 2 minutes of inactivity
  private bool _isRunning;

  public AppCommunicationServer(IGhostBus bus, HealthMonitor healthMonitor, StateManager stateManager)
  {
    _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
    _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
  }

  /// <summary>
  /// Start the communication server
  /// </summary>
  public async Task StartAsync(CancellationToken cancellationToken)
  {
    await _lock.WaitAsync(cancellationToken);
    try
    {
      if (_isRunning) return;
      _isRunning = true;

      // Start listeners for different message types
      _ = Task.Run(() => ListenForHeartbeatsAsync(_cts.Token), _cts.Token);
      _ = Task.Run(() => ListenForMetricsAsync(_cts.Token), _cts.Token);
      _ = Task.Run(() => ListenForHealthUpdatesAsync(_cts.Token), _cts.Token);
      _ = Task.Run(() => ListenForSystemEventsAsync(_cts.Token), _cts.Token);

      L.LogInfo("App communication server started");
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Stop the communication server
  /// </summary>
  public async Task StopAsync()
  {
    await _lock.WaitAsync();
    try
    {
      if (!_isRunning) return;
      _isRunning = false;

      // Cancel all listeners
      _cts.Cancel();
      _connections.Clear();

      L.LogInfo("App communication server stopped");
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Register a ghost app with the communication server
  /// </summary>
  public async Task RegisterAppAsync(ProcessRegistration registration)
  {
    if (registration == null) throw new ArgumentNullException(nameof(registration));

    await _lock.WaitAsync();
    try
    {
      var connectionInfo = new AppConnectionInfo
      {
          Id = registration.Id,
          Metadata = new ProcessMetadata(
              registration.Name,
              registration.Type,
              registration.Version,
              registration.Environment,
              registration.Configuration
          ),
          Status = "Registered",
          LastSeen = DateTime.UtcNow
      };

      _connections[registration.Id] = connectionInfo;
      L.LogInfo($"Registered app connection: {registration.Name} ({registration.Id})");
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Get all active connections
  /// </summary>
  public IEnumerable<AppConnectionInfo> GetActiveConnections()
  {
    return _connections.Values
        .Where(c => DateTime.UtcNow - c.LastSeen < _connectionTimeout)
        .ToList();
  }

  /// <summary>
  /// Get connection info for a specific app by ID
  /// </summary>
  public AppConnectionInfo GetConnectionInfoById(string id)
  {
    if (string.IsNullOrEmpty(id)) return null;

    _connections.TryGetValue(id, out var connectionInfo);
    return connectionInfo;
  }

  /// <summary>
  /// Check connections for timeouts and update status
  /// </summary>
  public async Task CheckConnectionsAsync()
  {
    await _lock.WaitAsync();
    try
    {
      var now = DateTime.UtcNow;
      foreach (var connection in _connections.Values)
      {
        // Update connection status based on last seen time
        if (now - connection.LastSeen > _connectionTimeout && connection.Status != "Disconnected")
        {
          connection.Status = "Disconnected";
          L.LogInfo($"App disconnected due to timeout: {connection.Metadata.Name} ({connection.Id})");

          // Publish disconnection event
          await PublishConnectionStatusChangeAsync(connection, "Disconnected");
        }
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Publish connection status change event
  /// </summary>
  private async Task PublishConnectionStatusChangeAsync(AppConnectionInfo connection, string status)
  {
    try
    {
      var statusEvent = new SystemEvent
      {
          Type = $"connection.{status.ToLower()}",
          ProcessId = connection.Id,
          Timestamp = DateTime.UtcNow,
          Data = null
      };

      await _bus.PublishAsync("ghost:events", statusEvent);
    }
    catch (Exception ex)
    {
      L.LogError(ex, $"Error publishing connection status change for {connection.Id}");
    }
  }

  /// <summary>
  /// Listen for heartbeat messages from ghost apps
  /// </summary>
  private async Task ListenForHeartbeatsAsync(CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var message in _bus.SubscribeAsync<object>("ghost:health:*", cancellationToken))
      {
        try
        {
          var topic = _bus.GetLastTopic();
          var appId = topic.Substring("ghost:health:".Length);

          // Try to deserialize the message using MemoryPack
          HeartbeatMessage heartbeat = null;

          if (message is byte[] memoryPackBytes)
          {
            heartbeat = MemoryPackSerializer.Deserialize<HeartbeatMessage>(memoryPackBytes);
          }
          else if (message is HeartbeatMessage directHeartbeat)
          {
            heartbeat = directHeartbeat;
          }
          else
          {
            // Try to convert the message to bytes and deserialize
            try
            {
              byte[] serialized = MemoryPackSerializer.Serialize(message);
              heartbeat = MemoryPackSerializer.Deserialize<HeartbeatMessage>(serialized);
            }
            catch
            {
              L.LogWarn($"Could not deserialize heartbeat message for {appId}");
            }
          }

          if (heartbeat != null)
          {
            await UpdateConnectionFromHeartbeatAsync(heartbeat);
          }
        }
        catch (Exception ex)
        {
          L.LogError(ex, "Error processing heartbeat message");
        }
      }
    }
    catch (OperationCanceledException)
    {
      // Normal cancellation
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Fatal error in heartbeat listener");
    }
  }

  /// <summary>
  /// Update connection info from a heartbeat message
  /// </summary>
  private async Task UpdateConnectionFromHeartbeatAsync(HeartbeatMessage heartbeat)
  {
    await _lock.WaitAsync();
    try
    {
      if (heartbeat == null) return;

      if (_connections.TryGetValue(heartbeat.Id, out var connection))
      {
        var wasDisconnected = connection.Status == "Disconnected";

        // Update connection
        connection.Status = heartbeat.Status;
        connection.LastSeen = DateTime.UtcNow;

        // If reconnected, publish event
        if (wasDisconnected)
        {
          L.LogInfo($"App reconnected: {connection.Metadata.Name} ({connection.Id})");
          await PublishConnectionStatusChangeAsync(connection, "Connected");
        }
      }
      else
      {
        // Auto-register unknown connections
        var newConnection = new AppConnectionInfo
        {
            Id = heartbeat.Id,
            Metadata = new ProcessMetadata(
                heartbeat.Id, // Use ID as name since we don't have better info
                "unknown",
                "1.0.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>
                {
                    ["AppType"] = heartbeat.AppType ?? "unknown"
                }
            ),
            Status = heartbeat.Status,
            LastSeen = DateTime.UtcNow
        };

        _connections[heartbeat.Id] = newConnection;
        L.LogInfo($"Auto-registered new app connection: {heartbeat.Id}");
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Listen for metrics messages from ghost apps
  /// </summary>
  private async Task ListenForMetricsAsync(CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var message in _bus.SubscribeAsync<object>("ghost:metrics:*", cancellationToken))
      {
        try
        {
          var topic = _bus.GetLastTopic();
          var appId = topic.Substring("ghost:metrics:".Length);

          // Skip daemon's own metrics
          if (appId == "ghost-daemon") continue;

          // Try to deserialize the message using MemoryPack
          ProcessMetrics metrics = null;

          if (message is byte[] memoryPackBytes)
          {
            metrics = MemoryPackSerializer.Deserialize<ProcessMetrics>(memoryPackBytes);
          }
          else if (message is ProcessMetrics directMetrics)
          {
            metrics = directMetrics;
          }
          else
          {
            // Try to convert the message to bytes and deserialize
            try
            {
              byte[] serialized = MemoryPackSerializer.Serialize(message);
              metrics = MemoryPackSerializer.Deserialize<ProcessMetrics>(serialized);
            }
            catch
            {
              L.LogWarn($"Could not deserialize metrics message for {appId}");
            }
          }

          if (metrics != null)
          {
            await UpdateConnectionMetricsAsync(appId, metrics);
          }
        }
        catch (Exception ex)
        {
          L.LogError(ex, "Error processing metrics message");
        }
      }
    }
    catch (OperationCanceledException)
    {
      // Normal cancellation
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Fatal error in metrics listener");
    }
  }

  /// <summary>
  /// Update connection metrics
  /// </summary>
  private async Task UpdateConnectionMetricsAsync(string appId, ProcessMetrics metrics)
  {
    await _lock.WaitAsync();
    try
    {
      if (_connections.TryGetValue(appId, out var connection))
      {
        var wasDisconnected = connection.Status == "Disconnected";

        // Update connection
        connection.LastMetrics = metrics;
        connection.LastSeen = DateTime.UtcNow;
        connection.Status = "Running"; // If we're getting metrics, the app is running

        // If reconnected, publish event
        if (wasDisconnected)
        {
          L.LogInfo($"App reconnected via metrics: {connection.Metadata.Name} ({connection.Id})");
          await PublishConnectionStatusChangeAsync(connection, "Connected");
        }

        // Save metrics to state manager
        var appType = connection.Metadata.Configuration.TryGetValue("AppType", out var type) ? type : "unknown";
        await _stateManager.SaveProcessMetricsAsync(appId, metrics, appType);
      }
      else
      {
        // Auto-register unknown connections with minimal info
        var newConnection = new AppConnectionInfo
        {
            Id = appId,
            Metadata = new ProcessMetadata(
                appId, // Use ID as name since we don't have better info
                "unknown",
                "1.0.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>
                {
                    ["AppType"] = "unknown"
                }
            ),
            Status = "Running",
            LastSeen = DateTime.UtcNow,
            LastMetrics = metrics
        };

        _connections[appId] = newConnection;
        L.LogInfo($"Auto-registered new app connection from metrics: {appId}");

        // Save metrics to state manager
        await _stateManager.SaveProcessMetricsAsync(appId, metrics, "unknown");
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Listen for health status updates from ghost apps
  /// </summary>
  private async Task ListenForHealthUpdatesAsync(CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var message in _bus.SubscribeAsync<object>("ghost:health:*", cancellationToken))
      {
        try
        {
          var topic = _bus.GetLastTopic();
          var appId = topic.Substring("ghost:health:".Length);

          // Try to deserialize the message using MemoryPack
          HealthStatusMessage healthStatus = null;

          if (message is byte[] memoryPackBytes)
          {
            healthStatus = MemoryPackSerializer.Deserialize<HealthStatusMessage>(memoryPackBytes);
          }
          else if (message is HealthStatusMessage directStatus)
          {
            healthStatus = directStatus;
          }
          else
          {
            // Try to convert the message to bytes and deserialize
            try
            {
              byte[] serialized = MemoryPackSerializer.Serialize(message);
              healthStatus = MemoryPackSerializer.Deserialize<HealthStatusMessage>(serialized);
            }
            catch
            {
              // This might be a heartbeat, which is handled separately
            }
          }

          if (healthStatus != null)
          {
            await UpdateConnectionHealthStatusAsync(healthStatus);
          }
        }
        catch (Exception ex)
        {
          L.LogError(ex, "Error processing health status message");
        }
      }
    }
    catch (OperationCanceledException)
    {
      // Normal cancellation
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Fatal error in health status listener");
    }
  }

  /// <summary>
  /// Update connection health status
  /// </summary>
  private async Task UpdateConnectionHealthStatusAsync(HealthStatusMessage healthStatus)
  {
    await _lock.WaitAsync();
    try
    {
      if (healthStatus == null) return;

      if (_connections.TryGetValue(healthStatus.Id, out var connection))
      {
        var wasDisconnected = connection.Status == "Disconnected";

        // Update connection
        connection.Status = healthStatus.Status;
        connection.LastMessage = healthStatus.Message;
        connection.LastSeen = DateTime.UtcNow;

        // If reconnected, publish event
        if (wasDisconnected)
        {
          L.LogInfo($"App reconnected via health status: {connection.Metadata.Name} ({connection.Id})");
          await PublishConnectionStatusChangeAsync(connection, "Connected");
        }
      }
      else
      {
        // Auto-register unknown connections
        var newConnection = new AppConnectionInfo
        {
            Id = healthStatus.Id,
            Metadata = new ProcessMetadata(
                healthStatus.Id, // Use ID as name since we don't have better info
                "unknown",
                "1.0.0",
                new Dictionary<string, string>(),
                new Dictionary<string, string>
                {
                    ["AppType"] = healthStatus.AppType ?? "unknown"
                }
            ),
            Status = healthStatus.Status,
            LastMessage = healthStatus.Message,
            LastSeen = DateTime.UtcNow
        };

        _connections[healthStatus.Id] = newConnection;
        L.LogInfo($"Auto-registered new app connection from health status: {healthStatus.Id}");
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Listen for system events from ghost apps
  /// </summary>
  private async Task ListenForSystemEventsAsync(CancellationToken cancellationToken)
  {
    try
    {
      await foreach (var systemEvent in _bus.SubscribeAsync<SystemEvent>("ghost:events", cancellationToken))
      {
        try
        {
          if (systemEvent == null) continue;

          switch (systemEvent.Type)
          {
            case "process.registered":
              await HandleProcessRegistrationEventAsync(systemEvent);
              break;
            case "process.stopped":
              await HandleProcessStoppedEventAsync(systemEvent);
              break;
            case "process.crashed":
              await HandleProcessCrashedEventAsync(systemEvent);
              break;
          }
        }
        catch (Exception ex)
        {
          L.LogError(ex, "Error processing system event");
        }
      }
    }
    catch (OperationCanceledException)
    {
      // Normal cancellation
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Fatal error in system event listener");
    }
  }

  /// <summary>
  /// Handle process registration event
  /// </summary>
  private async Task HandleProcessRegistrationEventAsync(SystemEvent systemEvent)
  {
    if (systemEvent?.Data == null) return;

    try
    {
      // Try to deserialize registration data from the event
      ProcessRegistration registration = null;

      if (systemEvent.Data is byte[] memoryPackBytes)
      {
        registration = MemoryPackSerializer.Deserialize<ProcessRegistration>(memoryPackBytes);
      }
      else
      {
        // Try to serialize the data object and then deserialize as ProcessRegistration
        byte[] serialized = MemoryPackSerializer.Serialize(systemEvent.Data);
        registration = MemoryPackSerializer.Deserialize<ProcessRegistration>(serialized);
      }

      if (registration != null)
      {
        // Register app with communication server
        await RegisterAppAsync(registration);
      }
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Error handling process registration event");
    }
  }

  /// <summary>
  /// Handle process stopped event
  /// </summary>
  private async Task HandleProcessStoppedEventAsync(SystemEvent systemEvent)
  {
    if (string.IsNullOrEmpty(systemEvent?.ProcessId)) return;

    await _lock.WaitAsync();
    try
    {
      if (_connections.TryGetValue(systemEvent.ProcessId, out var connection))
      {
        connection.Status = "Stopped";
        L.LogInfo($"App stopped: {connection.Metadata.Name} ({connection.Id})");

        // Publish stopped event
        await PublishConnectionStatusChangeAsync(connection, "Stopped");
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  /// <summary>
  /// Handle process crashed event
  /// </summary>
  private async Task HandleProcessCrashedEventAsync(SystemEvent systemEvent)
  {
    if (string.IsNullOrEmpty(systemEvent?.ProcessId)) return;

    await _lock.WaitAsync();
    try
    {
      if (_connections.TryGetValue(systemEvent.ProcessId, out var connection))
      {
        connection.Status = "Crashed";
        L.LogWarn($"App crashed: {connection.Metadata.Name} ({connection.Id})");

        // Publish crashed event
        await PublishConnectionStatusChangeAsync(connection, "Crashed");
      }
    }
    finally
    {
      _lock.Release();
    }
  }
  public async Task RegisterConnectionAsync(AppConnectionInfo connectionInfo)
  {
    if (connectionInfo == null) throw new ArgumentNullException(nameof(connectionInfo));

    await _lock.WaitAsync();
    try
    {
      _connections[connectionInfo.Id] = connectionInfo;
      L.LogInfo($"Registered app connection: {connectionInfo.Metadata.Name} ({connectionInfo.Id})");
    }
    finally
    {
      _lock.Release();
    }
  }
  public void UpdateDaemonMetrics(ProcessMetrics daemonMetrics)
  {
    if (daemonMetrics == null) throw new ArgumentNullException(nameof(daemonMetrics));

    _connections["ghost-daemon"].LastMetrics = daemonMetrics;
    _connections["ghost-daemon"].LastSeen = DateTime.UtcNow;

    // Save metrics to state manager
    var appType = "daemon";
    _stateManager.SaveProcessMetricsAsync("ghost-daemon", daemonMetrics, appType);
  }
}
