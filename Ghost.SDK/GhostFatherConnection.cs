using Ghost.Core;
using MemoryPack;
using Ghost.Core.Data;
using System.Diagnostics;
using Ghost.Core.Storage;

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
        private bool _isDisposed;

        /// <summary>
        /// Unique identifier for this connection
        /// </summary>
        public string Id => _id;

        /// <summary>
        /// Process metadata associated with this connection
        /// </summary>
        public ProcessMetadata Metadata => _metadata;

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

            // Create timers but don't start them yet
            _heartbeatTimer = new Timer(SendHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
            _metricsTimer = new Timer(SendMetrics, null, Timeout.Infinite, Timeout.Infinite);

            G.LogDebug($"Created GhostFather connection with ID: {_id}");
        }

        /// <summary>
        /// Creates a bus connection for communication
        /// </summary>
        private IGhostBus CreateBusConnection()
        {
            try
            {
                // Create a local file-based cache for bus
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

            try
            {
                // Register with GhostFather
                await RegisterAsync();

                // Start sending heartbeats every 30 seconds
                _heartbeatTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(30));

                // Start sending metrics every 5 seconds
                _metricsTimer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(5));

                G.LogDebug("Started reporting to GhostFather");
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Failed to start reporting to GhostFather");
            }
        }

        /// <summary>
        /// Sends a heartbeat message to indicate the process is alive
        /// </summary>
        private async void SendHeartbeat(object state)
        {
            if (_isDisposed || _bus == null) return;

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

                // Serialize using MemoryPack
                byte[] heartbeatBytes = MemoryPackSerializer.Serialize(heartbeat);

                await _bus.PublishAsync($"ghost:health:{_id}", heartbeatBytes);
            }
            catch (Exception ex)
            {
                G.LogWarn($"Error sending heartbeat: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends current process metrics to GhostFather
        /// </summary>
        private async void SendMetrics(object state)
        {
            if (_isDisposed || _bus == null) return;

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

                // Serialize using MemoryPack
                byte[] metricsBytes = MemoryPackSerializer.Serialize(metrics);

                await _bus.PublishAsync($"ghost:metrics:{_id}", metricsBytes);
            }
            catch (Exception ex)
            {
                G.LogWarn($"Error sending metrics: {ex.Message}");
            }
        }

        /// <summary>
        /// Manually reports current process metrics
        /// </summary>
        public async Task ReportMetricsAsync()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));
            if (_bus == null) throw new InvalidOperationException("Bus connection not initialized");

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

                // Serialize using MemoryPack
                byte[] metricsBytes = MemoryPackSerializer.Serialize(metrics);

                await _bus.PublishAsync($"ghost:metrics:{_id}", metricsBytes);
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Failed to report metrics");
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
            if (_bus == null) throw new InvalidOperationException("Bus connection not initialized");
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

                // Serialize using MemoryPack
                byte[] healthBytes = MemoryPackSerializer.Serialize(healthInfo);

                await _bus.PublishAsync($"ghost:health:{_id}", healthBytes);
                G.LogDebug($"Reported health status: {status}");
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Failed to report health status");
            }
        }

        /// <summary>
        /// Registers this process with GhostFather
        /// </summary>
        private async Task RegisterAsync()
        {
            if (_bus == null || _metadata == null)
                throw new InvalidOperationException("Connection not properly initialized");

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

                var systemEvent = new SystemEvent
                {
                    Type = "process.registered",
                    ProcessId = _id,
                    Data = registrationBytes,
                    Timestamp = DateTime.UtcNow
                };

                // Send registration event
                await _bus.PublishAsync("ghost:events", systemEvent);
                G.LogInfo($"Registered with GhostFather as {_metadata.Name} ({_id})");
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Failed to register with GhostFather");
                throw;
            }
        }

        /// <summary>
        /// Disposes the connection and reports process stopped
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_isDisposed) return;

            try
            {
                _isDisposed = true;

                // Stop timers if they exist
                _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _metricsTimer?.Change(Timeout.Infinite, Timeout.Infinite);

                // Report process stopped if bus exists
                if (_bus != null)
                {
                    var systemEvent = new SystemEvent
                    {
                        Type = "process.stopped",
                        ProcessId = _id,
                        Timestamp = DateTime.UtcNow
                    };

                    await _bus.PublishAsync("ghost:events", systemEvent);
                    G.LogInfo($"Reported process stopped: {_id}");
                }

                // Clean up resources
                if (_heartbeatTimer != null)
                    await _heartbeatTimer.DisposeAsync();

                if (_metricsTimer != null)
                    await _metricsTimer.DisposeAsync();

                if (_bus is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Error disposing GhostFather connection");
            }
        }
    }

    /// <summary>
    /// Serializable message for heartbeats
    /// </summary>
    [MemoryPackable]
    public partial class HeartbeatMessage
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public DateTime Timestamp { get; set; }
        public string AppType { get; set; }
    }

    /// <summary>
    /// Serializable message for health status
    /// </summary>
    [MemoryPackable]
    public partial class HealthStatusMessage
    {
        public string Id { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public string AppType { get; set; }
        public DateTime Timestamp { get; set; }
    }
}