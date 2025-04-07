using Ghost.Core.Data;
using System.Diagnostics;
using System.Text.Json;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Ghost.Father.Ghost.Core.Monitoring;

namespace Ghost.SDK;

public class GhostFatherConnection : IAsyncDisposable
{
    private readonly IGhostBus _bus;
    private readonly string _id;
    private readonly ProcessMetadata _metadata;
    private readonly Timer _heartbeatTimer;
    private readonly Timer _metricsTimer;
    private bool _isDisposed;

    public string Id => _id;
    public ProcessMetadata Metadata => _metadata;

    public GhostFatherConnection(ProcessMetadata metadata)
    {
        _id = $"app-{Guid.NewGuid()}";
        _metadata = metadata;
        _bus = CreateBusConnection();

        // Create timers but don't start them yet
        _heartbeatTimer = new Timer(SendHeartbeat, null, Timeout.Infinite, Timeout.Infinite);
        _metricsTimer = new Timer(SendMetrics, null, Timeout.Infinite, Timeout.Infinite);

        G.LogDebug($"Created GhostFather connection with ID: {_id}");
    }

    private IGhostBus CreateBusConnection()
    {
        try
        {
            // Create a local file-based cache for bus
            var tempPath = Path.Combine(
                    Path.GetTempPath(),
                    "ghost",
                    "cache",
                    Process.GetCurrentProcess().Id.ToString());

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

    public async Task StartReporting()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

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

    private async void SendHeartbeat(object state)
    {
        if (_isDisposed) return;

        try
        {
            await _bus.PublishAsync($"ghost:health:{_id}", new
            {
                    Id = _id,
                    Status = "Running",
                    Timestamp = DateTime.UtcNow,
                    AppType = _metadata.Configuration["AppType"]
            });
        }
        catch (Exception ex)
        {
            G.LogWarn($"Error sending heartbeat: {ex.Message}");
        }
    }

    private async void SendMetrics(object state)
    {
        if (_isDisposed) return;

        try
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();

            var metrics = new ProcessMetrics(
                    processId: _id,
                    cpuPercentage: 0, // CPU usage calculation would need more logic
                    memoryBytes: process.WorkingSet64,
                    threadCount: process.Threads.Count,
                    handleCount: process.HandleCount,
                    gcTotalMemory: GC.GetTotalMemory(false),
                    gen0Collections: GC.CollectionCount(0),
                    gen1Collections: GC.CollectionCount(1),
                    gen2Collections: GC.CollectionCount(2),
                    timestamp: DateTime.UtcNow
            );

            await _bus.PublishAsync($"ghost:metrics:{_id}", metrics);
        }
        catch (Exception ex)
        {
            G.LogWarn($"Error sending metrics: {ex.Message}");
        }
    }

    public async Task ReportMetricsAsync()
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

        try
        {
            var process = Process.GetCurrentProcess();
            process.Refresh();

            var metrics = new ProcessMetrics(
                    processId: _id,
                    cpuPercentage: 0, // CPU usage calculation would need more logic
                    memoryBytes: process.WorkingSet64,
                    threadCount: process.Threads.Count,
                    handleCount: process.HandleCount,
                    gcTotalMemory: GC.GetTotalMemory(false),
                    gen0Collections: GC.CollectionCount(0),
                    gen1Collections: GC.CollectionCount(1),
                    gen2Collections: GC.CollectionCount(2),
                    timestamp: DateTime.UtcNow
            );

            await _bus.PublishAsync($"ghost:metrics:{_id}", metrics);
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to report metrics");
        }
    }

    public async Task ReportHealthAsync(string status, string message)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

        try
        {
            var healthInfo = new
            {
                    Id = _id,
                    Status = status,
                    Message = message,
                    AppType = _metadata.Configuration["AppType"],
                    Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync($"ghost:health:{_id}", healthInfo);
            G.LogDebug($"Reported health status: {status}");
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to report health status");
        }
    }

    private async Task RegisterAsync()
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

            // Create a system event for process registration
            var systemEvent = new SystemEvent
            {
                    Type = "process.registered",
                    ProcessId = _id,
                    Data = JsonSerializer.Serialize(registration),
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

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        try
        {
            _isDisposed = true;

            // Stop timers
            _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _metricsTimer.Change(Timeout.Infinite, Timeout.Infinite);

            // Report process stopped
            var systemEvent = new SystemEvent
            {
                    Type = "process.stopped",
                    ProcessId = _id,
                    Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync("ghost:events", systemEvent);
            G.LogInfo($"Reported process stopped: {_id}");

            // Clean up resources
            await _heartbeatTimer.DisposeAsync();
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
