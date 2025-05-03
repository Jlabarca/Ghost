using Ghost.Core;
using MemoryPack;
using Ghost.Core.Data;
using System.Diagnostics;
using Ghost.Core.Storage;
using System.Threading.Channels;

namespace Ghost
{
    /// <summary>
    /// Handles connection and communication with the GhostFather daemon process
    /// using MemoryPack for efficient serialization
    /// </summary>
    public class GhostFatherConnection : IAsyncDisposable
    {
        private readonly IGhostBus _bus;
        private readonly string _id;
        private readonly ProcessMetadata _metadata;
        private readonly Timer _heartbeatTimer;
        private readonly Timer _metricsTimer;
        private readonly Timer _reconnectTimer;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly Channel<object> _messageQueue;
        private bool _isDisposed;
        private bool _isConnected;
        private int _reconnectAttempts;
        private readonly int _maxReconnectAttempts = 5;
        private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Unique identifier for this connection
        /// </summary>
        public string Id => _id;

        /// <summary>
        /// Process metadata associated with this connection
        /// </summary>
        public ProcessMetadata Metadata => _metadata;

        /// <summary>
        /// Whether the connection is currently active
        /// </summary>
        public bool IsConnected => _isConnected;

        /// <summary>
        /// Last error encountered during communication
        /// </summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Event raised when connection status changes
        /// </summary>
        public event EventHandler<ConnectionStatusEventArgs> ConnectionStatusChanged;

        /// <summary>
        /// Private default constructor - not for public use
        /// </summary>
        internal GhostFatherConnection()
        {
            // This constructor should not be used directly
            _id = string.Empty;
            _metadata = null;
            _bus = null;
            _heartbeatTimer = null;
            _metricsTimer = null;
            _reconnectTimer = null;
            _messageQueue = Channel.CreateUnbounded<object>();
        }

        /// <summary>
        /// Creates a new connection to GhostFather with the specified metadata
        /// </summary>
        /// <param name="metadata">Metadata describing the current process</param>
        public GhostFatherConnection(ProcessMetadata metadata)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            _id = $"app-{Guid.NewGuid()}";
            _metadata = metadata;
            _bus = CreateBusConnection();
            _messageQueue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions
            {
                    SingleReader = true,
                    SingleWriter = false
            });

            // Create timers but don't start them yet
            _heartbeatTimer = new Timer(SendHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
            _metricsTimer = new Timer(SendMetrics, null, Timeout.Infinite, Timeout.Infinite);
            _reconnectTimer = new Timer(TryReconnect, null, Timeout.Infinite, Timeout.Infinite);

            // Start message processor
            _ = ProcessMessageQueueAsync();

            G.LogDebug($"Created GhostFather connection with ID: {_id}");
        }

        /// <summary>
        /// Creates a bus connection for communication
        /// </summary>
        private IGhostBus CreateBusConnection()
        {
            try
            {
                //Create a local file-based cache for bus
                var tempPath = Path.Combine(
                        Path.GetTempPath(), "ghost", "cache", Process.GetCurrentProcess().Id.ToString());
                Directory.CreateDirectory(tempPath);
                var cache = new LocalCache(tempPath);
                return new GhostBus(cache);
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Failed to create GhostBus connection");
                throw;
            }
        }

        /// <summary>
        /// Starts reporting metrics and heartbeats to GhostFather
        /// </summary>
        public async Task StartReporting()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));
            if (_bus == null) throw new InvalidOperationException("Bus connection not initialized");

            await _lock.WaitAsync();
            try
            {
                // Check connection to daemon first
                if (!await CheckConnectionAsync())
                {
                    _isConnected = false;
                    G.LogWarn("Could not connect to GhostFather daemon, will try reconnecting...");
                    _reconnectTimer.Change(TimeSpan.Zero, _reconnectDelay);
                    return;
                }

                _isConnected = true;
                _reconnectAttempts = 0;
                _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);

                // Register with GhostFather
                await RegisterWithDaemonAsync();

                // Start sending heartbeats every 30 seconds
                _heartbeatTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));

                // Start sending metrics every 5 seconds
                _metricsTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));

                G.LogInfo("Started reporting to GhostFather");
                OnConnectionStatusChanged(true, null);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                G.LogError(ex, "Failed to start reporting to GhostFather");

                // Start reconnection attempts
                _reconnectTimer.Change(TimeSpan.Zero, _reconnectDelay);
                OnConnectionStatusChanged(false, ex.Message);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Check if the daemon is reachable
        /// </summary>
        private async Task<bool> CheckConnectionAsync()
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
                await _bus.PublishAsync("ghost:commands", pingCommand);

                // Wait for response with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

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
            if (_isDisposed || _isConnected) return;

            await _lock.WaitAsync();
            try
            {
                if (_isDisposed || _isConnected) return;

                _reconnectAttempts++;
                G.LogInfo($"Attempting to reconnect to GhostFather daemon (attempt {_reconnectAttempts}/{_maxReconnectAttempts})...");

                if (await CheckConnectionAsync())
                {
                    _isConnected = true;
                    _reconnectAttempts = 0;
                    _reconnectTimer.Change(Timeout.Infinite, Timeout.Infinite);

                    // Re-register with daemon
                    await RegisterWithDaemonAsync();

                    // Restart timers
                    _heartbeatTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));
                    _metricsTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));

                    G.LogInfo("Reconnected to GhostFather daemon");
                    OnConnectionStatusChanged(true, null);
                } else if (_reconnectAttempts >= _maxReconnectAttempts)
                {
                    G.LogWarn($"Failed to reconnect to GhostFather daemon after {_maxReconnectAttempts} attempts");
                    _reconnectTimer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)); // Slow down reconnect attempts
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                G.LogError(ex, "Error during reconnection attempt");
            }
            finally
            {
                _lock.Release();
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
                                    // Try to send the message
                                    if (!_isConnected)
                                    {
                                        // If not connected, queue the message back up
                                        await _messageQueue.Writer.WriteAsync(message);
                                        await Task.Delay(1000); // Wait a bit before rechecking connection
                                        break; // Break out of the inner loop to wait for connection
                                    }

                                    // Process message based on type
                                    if (message is SystemEvent systemEvent)
                                    {
                                        await _bus.PublishAsync("ghost:events", systemEvent);
                                    } else if (message is HeartbeatMessage heartbeat)
                                    {
                                        byte[] heartbeatBytes = MemoryPackSerializer.Serialize(heartbeat);
                                        await _bus.PublishAsync($"ghost:health:{_id}", heartbeatBytes);
                                    } else if (message is HealthStatusMessage healthStatus)
                                    {
                                        byte[] healthBytes = MemoryPackSerializer.Serialize(healthStatus);
                                        await _bus.PublishAsync($"ghost:health:{_id}", healthBytes);
                                    } else if (message is ProcessMetrics metrics)
                                    {
                                        byte[] metricsBytes = MemoryPackSerializer.Serialize(metrics);
                                        await _bus.PublishAsync($"ghost:metrics:{_id}", metricsBytes);
                                    } else if (message is SystemCommand command)
                                    {
                                        await _bus.PublishAsync("ghost:commands", command);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    LastError = ex.Message;
                                    G.LogWarn($"Error processing message: {ex.Message}");

                                    // If the error might be connection-related, mark as disconnected
                                    if (!_isDisposed && _isConnected)
                                    {
                                        await _lock.WaitAsync();
                                        try
                                        {
                                            _isConnected = false;
                                            OnConnectionStatusChanged(false, ex.Message);

                                            // Queue the message for retry
                                            await _messageQueue.Writer.WriteAsync(message);

                                            // Start reconnection attempts
                                            _reconnectTimer.Change(TimeSpan.Zero, _reconnectDelay);
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
        /// Sends a heartbeat message to indicate the process is alive
        /// </summary>
        private async void SendHeartbeat(object state)
        {
            if (_isDisposed) return;

            try
            {
                // Create a serializable heartbeat message
                var heartbeat = new HeartbeatMessage
                {
                        Id = _id,
                        Status = "Running",
                        Timestamp = DateTime.UtcNow,
                        AppType = _metadata?.Configuration != null && _metadata.Configuration.TryGetValue("AppType", out var appType) ? appType : "unknown"
                };

                // Add to the message queue for processing
                await _messageQueue.Writer.WriteAsync(heartbeat);
            }
            catch (Exception ex)
            {
                G.LogWarn($"Error queuing heartbeat: {ex.Message}");
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

                var metrics = new ProcessMetrics(
                        ProcessId: _id,
                        CpuPercentage: 0, // CPU usage calculation would need more logic
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
                await _messageQueue.Writer.WriteAsync(metrics);
            }
            catch (Exception ex)
            {
                G.LogWarn($"Error queuing metrics: {ex.Message}");
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
                        CpuPercentage: 0, // CPU usage calculation would need more logic
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
                await _messageQueue.Writer.WriteAsync(metrics);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                G.LogError(ex, "Failed to report metrics");
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
                        AppType = _metadata?.Configuration != null && _metadata.Configuration.TryGetValue("AppType", out var appType) ? appType : "unknown",
                        Timestamp = DateTime.UtcNow
                };

                // Add to the message queue for processing
                await _messageQueue.Writer.WriteAsync(healthInfo);
                G.LogDebug($"Queued health status report: {status}");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                G.LogError(ex, "Failed to report health status");
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

                // Queue the command for sending
                await _messageQueue.Writer.WriteAsync(command);

                // Wait for response with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                try
                {
                    await foreach (var response in _bus.SubscribeAsync<CommandResponse>(responseChannel, cts.Token))
                    {
                        if (response.CommandId == commandId)
                        {
                            return response;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Timeout occurred
                    G.LogWarn($"Command {commandType} timed out");
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
            catch (Exception ex)
            {
                LastError = ex.Message;
                G.LogError(ex, $"Error sending command {commandType}");
                throw;
            }
        }

        /// <summary>
        /// Registers this process with GhostFather
        /// </summary>
        private async Task RegisterWithDaemonAsync()
        {
            try
            {
                // Create registration message with process info
                var registration = new ProcessRegistration
                {
                        Id = _id,
                        Name = _metadata.Name,
                        Type = _metadata.Type,
                        Version = _metadata.Version,
                        ExecutablePath = Process.GetCurrentProcess().MainModule?.FileName ?? "unknown",
                        Arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1)),
                        WorkingDirectory = Directory.GetCurrentDirectory(),
                        Environment = _metadata.Environment,
                        Configuration = _metadata.Configuration
                };

                // Serialize the registration using MemoryPack
                byte[] registrationBytes = MemoryPackSerializer.Serialize(registration);

                // Try the new API first - send a register command with serialized data
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

                // Queue the command for sending
                await _messageQueue.Writer.WriteAsync(registerCommand);

                // Also send as a system event for backward compatibility
                var systemEvent = new SystemEvent
                {
                        Type = "process.registered",
                        ProcessId = _id,
                        Data = registrationBytes,
                        Timestamp = DateTime.UtcNow
                };

                // Queue the event for sending
                await _messageQueue.Writer.WriteAsync(systemEvent);

                G.LogInfo($"Registered with GhostFather as {_metadata.Name} ({_id})");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                G.LogError(ex, "Failed to register with GhostFather");
                throw;
            }
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

                // Report process stopped if bus exists
                if (_bus != null && _isConnected)
                {
                    var systemEvent = new SystemEvent
                    {
                            Type = "process.stopped",
                            ProcessId = _id,
                            Timestamp = DateTime.UtcNow
                    };

                    // Try to send directly, don't queue since we're shutting down
                    try
                    {
                        await _bus.PublishAsync("ghost:events", systemEvent);
                        G.LogInfo($"Reported process stopped: {_id}");
                    }
                    catch (Exception ex)
                    {
                        G.LogWarn($"Error reporting process stopped: {ex.Message}");
                    }
                }

                // Complete the message queue
                _messageQueue.Writer.TryComplete();

                // Clean up resources
                if (_heartbeatTimer != null)
                    await _heartbeatTimer.DisposeAsync();

                if (_metricsTimer != null)
                    await _metricsTimer.DisposeAsync();

                if (_reconnectTimer != null)
                    await _reconnectTimer.DisposeAsync();

                if (_bus is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync();
                }

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

    /// <summary>
    /// Event arguments for connection status changes
    /// </summary>
    public class ConnectionStatusEventArgs : EventArgs
    {
        public bool IsConnected { get; }
        public string Error { get; }

        public ConnectionStatusEventArgs(bool isConnected, string error)
        {
            IsConnected = isConnected;
            Error = error;
        }
    }
}