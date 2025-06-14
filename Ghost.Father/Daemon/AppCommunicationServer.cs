using System.Collections.Concurrent;
using Ghost.Storage;
using MemoryPack;
namespace Ghost.Father.Daemon;

/// <summary>
///     Manages communication with ghost apps that connect to the daemon
/// </summary>
public class AppCommunicationServer
{
    private readonly IGhostBus _bus;
    private readonly ConcurrentDictionary<string, AppConnectionInfo> _connections = new ConcurrentDictionary<string, AppConnectionInfo>();
    private readonly TimeSpan _connectionTimeout = TimeSpan.FromMinutes(2); // Consider app disconnected after 2 minutes of inactivity
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly HealthMonitor _healthMonitor;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private readonly StateManager _stateManager;
    private bool _isRunning;

    public AppCommunicationServer(IGhostBus bus, HealthMonitor healthMonitor, StateManager stateManager)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
        _stateManager = stateManager ?? throw new ArgumentNullException(nameof(stateManager));
    }

// Add these enhanced logging methods to AppCommunicationServer.cs

/// <summary>
///     Start the communication server
/// </summary>
public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_isRunning)
            {
                return;
            }
            _isRunning = true;

            G.LogInfo("Starting app communication server...");
            G.LogInfo($"Bus type: {_bus.GetType().Name}");

            // Start listeners for different message types
            G.LogInfo("Starting heartbeat listener...");
            _ = Task.Run(() => ListenForHeartbeatsAsync(_cts.Token), _cts.Token);

            G.LogInfo("Starting metrics listener...");
            _ = Task.Run(() => ListenForMetricsAsync(_cts.Token), _cts.Token);

            G.LogInfo("Starting health updates listener...");
            _ = Task.Run(() => ListenForHealthUpdatesAsync(_cts.Token), _cts.Token);

            G.LogInfo("Starting system events listener...");
            _ = Task.Run(() => ListenForSystemEventsAsync(_cts.Token), _cts.Token);

            G.LogInfo("App communication server started successfully");
            G.LogInfo($"Currently tracking {_connections.Count} connections");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Listen for heartbeat messages from ghost apps
    /// </summary>
    private async Task ListenForHeartbeatsAsync(CancellationToken cancellationToken)
    {
        try
        {
            G.LogInfo("Heartbeat listener started - subscribing to ghost:health:*");

            await foreach (object? message in _bus.SubscribeAsync<object>("ghost:health:*", cancellationToken))
            {
                try
                {
                    string? topic = _bus.GetLastTopic();
                    string? appId = topic.Substring("ghost:health:".Length);

                    G.LogDebug($"Received heartbeat message from {appId} on topic {topic}");

                    // Try to deserialize the message using MemoryPack
                    HeartbeatMessage heartbeat = null;

                    if (message is byte[] memoryPackBytes)
                    {
                        G.LogDebug($"Deserializing heartbeat as byte array for {appId}");
                        heartbeat = MemoryPackSerializer.Deserialize<HeartbeatMessage>(memoryPackBytes);
                    }
                    else if (message is HeartbeatMessage directHeartbeat)
                    {
                        G.LogDebug($"Received direct heartbeat message for {appId}");
                        heartbeat = directHeartbeat;
                    }
                    else
                    {
                        // Try to convert the message to bytes and deserialize
                        try
                        {
                            G.LogDebug($"Attempting to serialize/deserialize heartbeat for {appId}");
                            byte[] serialized = MemoryPackSerializer.Serialize(message);
                            heartbeat = MemoryPackSerializer.Deserialize<HeartbeatMessage>(serialized);
                        }
                        catch (Exception ex)
                        {
                            G.LogWarn($"Could not deserialize heartbeat message for {appId}: {ex.Message}");
                        }
                    }

                    if (heartbeat != null)
                    {
                        G.LogInfo($"Processing heartbeat from {appId}: Status={heartbeat.Status}");
                        await UpdateConnectionFromHeartbeatAsync(heartbeat);
                    }
                    else
                    {
                        G.LogWarn($"Failed to deserialize heartbeat from {appId}");
                    }
                }
                catch (Exception ex)
                {
                    G.LogError(ex, "Error processing heartbeat message");
                }
            }
        }
        catch (OperationCanceledException)
        {
            G.LogInfo("Heartbeat listener cancelled");
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Fatal error in heartbeat listener");
        }
    }

    /// <summary>
    ///     Listen for metrics messages from ghost apps
    /// </summary>
    private async Task ListenForMetricsAsync(CancellationToken cancellationToken)
    {
        try
        {
            G.LogInfo("Metrics listener started - subscribing to ghost:metrics:*");

            await foreach (object? message in _bus.SubscribeAsync<object>("ghost:metrics:*", cancellationToken))
            {
                try
                {
                    string? topic = _bus.GetLastTopic();
                    string? appId = topic.Substring("ghost:metrics:".Length);

                    // Skip daemon's own metrics
                    if (appId == "ghost-daemon")
                    {
                        continue;
                    }

                    G.LogDebug($"Received metrics message from {appId} on topic {topic}");

                    // Try to deserialize the message using MemoryPack
                    ProcessMetrics metrics = null;

                    if (message is byte[] memoryPackBytes)
                    {
                        G.LogDebug($"Deserializing metrics as byte array for {appId}");
                        metrics = MemoryPackSerializer.Deserialize<ProcessMetrics>(memoryPackBytes);
                    }
                    else if (message is ProcessMetrics directMetrics)
                    {
                        G.LogDebug($"Received direct metrics message for {appId}");
                        metrics = directMetrics;
                    }
                    else
                    {
                        // Try to convert the message to bytes and deserialize
                        try
                        {
                            G.LogDebug($"Attempting to serialize/deserialize metrics for {appId}");
                            byte[] serialized = MemoryPackSerializer.Serialize(message);
                            metrics = MemoryPackSerializer.Deserialize<ProcessMetrics>(serialized);
                        }
                        catch (Exception ex)
                        {
                            G.LogWarn($"Could not deserialize metrics message for {appId}: {ex.Message}");
                        }
                    }

                    if (metrics != null)
                    {
                        G.LogDebug($"Processing metrics from {appId}: CPU={metrics.CpuPercentage:F1}%, Memory={metrics.MemoryBytes / 1024 / 1024}MB");
                        await UpdateConnectionMetricsAsync(appId, metrics);
                    }
                    else
                    {
                        G.LogWarn($"Failed to deserialize metrics from {appId}");
                    }
                }
                catch (Exception ex)
                {
                    G.LogError(ex, "Error processing metrics message");
                }
            }
        }
        catch (OperationCanceledException)
        {
            G.LogInfo("Metrics listener cancelled");
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Fatal error in metrics listener");
        }
    }

    /// <summary>
    ///     Listen for system events from ghost apps
    /// </summary>
    private async Task ListenForSystemEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            G.LogInfo("System events listener started - subscribing to ghost:events");

            await foreach (SystemEvent? systemEvent in _bus.SubscribeAsync<SystemEvent>("ghost:events", cancellationToken))
            {
                try
                {
                    if (systemEvent == null)
                    {
                        continue;
                    }

                    G.LogInfo($"Received system event: Type={systemEvent.Type}, ProcessId={systemEvent.ProcessId}");

                    switch (systemEvent.Type)
                    {
                        case "process.registered":
                            G.LogInfo($"Processing process registration event for {systemEvent.ProcessId}");
                            await HandleProcessRegistrationEventAsync(systemEvent);
                            break;
                        case "process.stopped":
                            G.LogInfo($"Processing process stopped event for {systemEvent.ProcessId}");
                            await HandleProcessStoppedEventAsync(systemEvent);
                            break;
                        case "process.crashed":
                            G.LogWarn($"Processing process crashed event for {systemEvent.ProcessId}");
                            await HandleProcessCrashedEventAsync(systemEvent);
                            break;
                        default:
                            G.LogDebug($"Unhandled system event type: {systemEvent.Type}");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    G.LogError(ex, "Error processing system event");
                }
            }
        }
        catch (OperationCanceledException)
        {
            G.LogInfo("System events listener cancelled");
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Fatal error in system event listener");
        }
    }

    /// <summary>
    ///     Get all active connections
    /// </summary>
    public IEnumerable<AppConnectionInfo> GetActiveConnections()
    {
        var activeConnections = _connections.Values
                .Where(c => DateTime.UtcNow - c.LastSeen < _connectionTimeout)
                .ToList();

        G.LogDebug($"Returning {activeConnections.Count} active connections out of {_connections.Count} total");

        return activeConnections;
    }

    /// <summary>
    ///     Stop the communication server
    /// </summary>
    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!_isRunning)
            {
                return;
            }
            _isRunning = false;

            // Cancel all listeners
            _cts.Cancel();
            _connections.Clear();

            G.LogInfo("App communication server stopped");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Register a ghost app with the communication server
    /// </summary>
    public async Task RegisterAppAsync(ProcessRegistration registration)
    {
        if (registration == null)
        {
            throw new ArgumentNullException(nameof(registration));
        }

        await _lock.WaitAsync();
        try
        {
            AppConnectionInfo? connectionInfo = new AppConnectionInfo
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
            G.LogInfo($"Registered app connection: {registration.Name} ({registration.Id})");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Get all active connections
    /// </summary>
    /// <summary>
    ///     Get connection info for a specific app by ID
    /// </summary>
    public AppConnectionInfo GetConnectionInfoById(string id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        _connections.TryGetValue(id, out AppConnectionInfo? connectionInfo);
        return connectionInfo;
    }

    /// <summary>
    ///     Check connections for timeouts and update status
    /// </summary>
    public async Task CheckConnectionsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            DateTime now = DateTime.UtcNow;
            foreach (AppConnectionInfo? connection in _connections.Values)
            {
                // Update connection status based on last seen time
                if (now - connection.LastSeen > _connectionTimeout && connection.Status != "Disconnected")
                {
                    connection.Status = "Disconnected";
                    G.LogInfo($"App disconnected due to timeout: {connection.Metadata.Name} ({connection.Id})");

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
    ///     Publish connection status change event
    /// </summary>
    private async Task PublishConnectionStatusChangeAsync(AppConnectionInfo connection, string status)
    {
        try
        {
            SystemEvent? statusEvent = new SystemEvent
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
            G.LogError(ex, $"Error publishing connection status change for {connection.Id}");
        }
    }

    /// <summary>
    ///     Listen for heartbeat messages from ghost apps
    /// </summary>
    /// <summary>
    ///     Update connection info from a heartbeat message
    /// </summary>
    private async Task UpdateConnectionFromHeartbeatAsync(HeartbeatMessage heartbeat)
    {
        await _lock.WaitAsync();
        try
        {
            if (heartbeat == null)
            {
                return;
            }

            if (_connections.TryGetValue(heartbeat.Id, out AppConnectionInfo? connection))
            {
                bool wasDisconnected = connection.Status == "Disconnected";

                // Update connection
                connection.Status = heartbeat.Status;
                connection.LastSeen = DateTime.UtcNow;

                // If reconnected, publish event
                if (wasDisconnected)
                {
                    G.LogInfo($"App reconnected: {connection.Metadata.Name} ({connection.Id})");
                    await PublishConnectionStatusChangeAsync(connection, "Connected");
                }
            }
            else
            {
                // Auto-register unknown connections
                AppConnectionInfo? newConnection = new AppConnectionInfo
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
                G.LogInfo($"Auto-registered new app connection: {heartbeat.Id}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Listen for metrics messages from ghost apps
    /// </summary>
    /// <summary>
    ///     Update connection metrics
    /// </summary>
    private async Task UpdateConnectionMetricsAsync(string appId, ProcessMetrics metrics)
    {
        await _lock.WaitAsync();
        try
        {
            if (_connections.TryGetValue(appId, out AppConnectionInfo? connection))
            {
                bool wasDisconnected = connection.Status == "Disconnected";

                // Update connection
                connection.LastMetrics = metrics;
                connection.LastSeen = DateTime.UtcNow;
                connection.Status = "Running"; // If we're getting metrics, the app is running

                // If reconnected, publish event
                if (wasDisconnected)
                {
                    G.LogInfo($"App reconnected via metrics: {connection.Metadata.Name} ({connection.Id})");
                    await PublishConnectionStatusChangeAsync(connection, "Connected");
                }

                // Save metrics to state manager
                string? appType = connection.Metadata.Configuration.TryGetValue("AppType", out string? type) ? type : "unknown";
                await _stateManager.SaveProcessMetricsAsync(appId, metrics, appType);
            }
            else
            {
                // Auto-register unknown connections with minimal info
                AppConnectionInfo? newConnection = new AppConnectionInfo
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
                G.LogInfo($"Auto-registered new app connection from metrics: {appId}");

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
    ///     Listen for health status updates from ghost apps
    /// </summary>
    private async Task ListenForHealthUpdatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (object? message in _bus.SubscribeAsync<object>("ghost:health:*", cancellationToken))
            {
                try
                {
                    string? topic = _bus.GetLastTopic();
                    string? appId = topic.Substring("ghost:health:".Length);

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
                    G.LogError(ex, "Error processing health status message");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Fatal error in health status listener");
        }
    }

    /// <summary>
    ///     Update connection health status
    /// </summary>
    private async Task UpdateConnectionHealthStatusAsync(HealthStatusMessage healthStatus)
    {
        await _lock.WaitAsync();
        try
        {
            if (healthStatus == null)
            {
                return;
            }

            if (_connections.TryGetValue(healthStatus.Id, out AppConnectionInfo? connection))
            {
                bool wasDisconnected = connection.Status == "Disconnected";

                // Update connection
                connection.Status = healthStatus.Status;
                connection.LastMessage = healthStatus.Message;
                connection.LastSeen = DateTime.UtcNow;

                // If reconnected, publish event
                if (wasDisconnected)
                {
                    G.LogInfo($"App reconnected via health status: {connection.Metadata.Name} ({connection.Id})");
                    await PublishConnectionStatusChangeAsync(connection, "Connected");
                }
            }
            else
            {
                // Auto-register unknown connections
                AppConnectionInfo? newConnection = new AppConnectionInfo
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
                G.LogInfo($"Auto-registered new app connection from health status: {healthStatus.Id}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Listen for system events from ghost apps
    /// </summary>
    /// <summary>
    ///     Handle process registration event
    /// </summary>
    private async Task HandleProcessRegistrationEventAsync(SystemEvent systemEvent)
    {
        if (systemEvent?.Data == null)
        {
            return;
        }

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
            G.LogError(ex, "Error handling process registration event");
        }
    }

    /// <summary>
    ///     Handle process stopped event
    /// </summary>
    private async Task HandleProcessStoppedEventAsync(SystemEvent systemEvent)
    {
        if (string.IsNullOrEmpty(systemEvent?.ProcessId))
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            if (_connections.TryGetValue(systemEvent.ProcessId, out AppConnectionInfo? connection))
            {
                connection.Status = "Stopped";
                G.LogInfo($"App stopped: {connection.Metadata.Name} ({connection.Id})");

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
    ///     Handle process crashed event
    /// </summary>
    private async Task HandleProcessCrashedEventAsync(SystemEvent systemEvent)
    {
        if (string.IsNullOrEmpty(systemEvent?.ProcessId))
        {
            return;
        }

        await _lock.WaitAsync();
        try
        {
            if (_connections.TryGetValue(systemEvent.ProcessId, out AppConnectionInfo? connection))
            {
                connection.Status = "Crashed";
                G.LogWarn($"App crashed: {connection.Metadata.Name} ({connection.Id})");

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
        if (connectionInfo == null)
        {
            throw new ArgumentNullException(nameof(connectionInfo));
        }

        await _lock.WaitAsync();
        try
        {
            _connections[connectionInfo.Id] = connectionInfo;
            G.LogInfo($"Registered app connection: {connectionInfo.Metadata.Name} ({connectionInfo.Id})");
        }
        finally
        {
            _lock.Release();
        }
    }
    public void UpdateDaemonMetrics(ProcessMetrics daemonMetrics)
    {
        if (daemonMetrics == null)
        {
            throw new ArgumentNullException(nameof(daemonMetrics));
        }

        _connections["ghost-daemon"].LastMetrics = daemonMetrics;
        _connections["ghost-daemon"].LastSeen = DateTime.UtcNow;

        // Save metrics to state manager
        string? appType = "daemon";
        _stateManager.SaveProcessMetricsAsync("ghost-daemon", daemonMetrics, appType);
    }
}
