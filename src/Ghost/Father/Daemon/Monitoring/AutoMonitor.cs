using Ghost.Core.Data;
using Ghost.Core.PM;
using Ghost.Core.Storage;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ProcessInfo = Ghost.Core.PM.ProcessInfo;
using ProcessMetrics = Ghost.Core.Monitoring.ProcessMetrics;

namespace Ghost.Father;

/// <summary>
/// Interface for automatic metrics collection and monitoring
/// </summary>
public interface IAutoMonitor : IAsyncDisposable
{
  Task StartAsync();
  Task StopAsync();
  Task TrackMetricAsync(string name, double value, Dictionary<string, string> tags = null);
  Task TrackEventAsync(string name, Dictionary<string, string> properties = null);
  Task<IEnumerable<AutoMonitor.MetricReading>> GetMetricsAsync(string name, DateTime start, DateTime end);
}

/// <summary>
/// Implementation of automatic metrics collection
/// </summary>
public class AutoMonitor : IAutoMonitor
{
    private readonly IGhostBus _bus;
    private readonly Timer _metricsTimer;
    private readonly ConcurrentDictionary<string, MetricValue> _metrics;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
    private volatile bool _isRunning;
    private SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public AutoMonitor(IGhostBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _metrics = new ConcurrentDictionary<string, MetricValue>();
        _metricsTimer = new Timer(CollectMetrics);
    }

    public async Task StartAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_isRunning || _disposed) return;
            _isRunning = true;
            _metricsTimer.Change(TimeSpan.Zero, _interval);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!_isRunning) return;
            _isRunning = false;
            await _metricsTimer.DisposeAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task TrackMetricAsync(string name, double value, Dictionary<string, string> tags = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));

        var metric = new MetricValue
        {
                Name = name,
                Value = value,
                Tags = tags ?? new Dictionary<string, string>(),
                Timestamp = DateTime.UtcNow
        };

        _metrics.AddOrUpdate(name, metric, (_, existing) => metric);

        // Publish metric immediately
        await PublishMetricAsync(metric);
    }

    public async Task TrackEventAsync(string name, Dictionary<string, string> properties = null)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));

        await _bus.PublishAsync("ghost:events", new SystemEvent
        {
                Type = "metric.event",
                Source = Process.GetCurrentProcess().Id.ToString(),
                Data = JsonSerializer.Serialize(new
                {
                        Name = name,
                        Properties = properties ?? new Dictionary<string, string>(),
                        Timestamp = DateTime.UtcNow
                })
        });
    }

    public async Task<IEnumerable<MetricReading>> GetMetricsAsync(string name, DateTime start, DateTime end)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));

        var readings = new List<MetricReading>();

        try
        {
            await foreach (var msg in _bus.SubscribeAsync<string>($"ghost:metrics:{name}"))
            {
                var reading = JsonSerializer.Deserialize<MetricReading>(msg);
                if (reading.Timestamp >= start && reading.Timestamp <= end)
                {
                    readings.Add(reading);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when subscription is cancelled
        }

        return readings;
    }

    private async void CollectMetrics(object state)
    {
        if (!_isRunning || _disposed) return;

        try
        {
            var process = Process.GetCurrentProcess();
            var metrics = new Dictionary<string, double>
            {
                    ["cpu.usage"] = await GetCpuUsageAsync(),
                    ["memory.private"] = process.PrivateMemorySize64,
                    ["memory.working"] = process.WorkingSet64,
                    ["memory.virtual"] = process.VirtualMemorySize64,
                    ["thread.count"] = process.Threads.Count,
                    ["handle.count"] = process.HandleCount,
                    ["gc.total.memory"] = GC.GetTotalMemory(false),
                    ["gc.collection.gen0"] = GC.CollectionCount(0),
                    ["gc.collection.gen1"] = GC.CollectionCount(1),
                    ["gc.collection.gen2"] = GC.CollectionCount(2)
            };

            foreach (var (name, value) in metrics)
            {
                await TrackMetricAsync(name, value);
            }
        }
        catch (Exception ex)
        {
            G.LogError("Error collecting metrics", ex);
        }
    }

    private async Task<double> GetCpuUsageAsync()
    {
        try
        {
            var startTime = DateTime.UtcNow;
            var startCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;

            await Task.Delay(100); // Sample over 100ms

            var endTime = DateTime.UtcNow;
            var endCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;

            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);

            return Math.Round(cpuUsageTotal * 100, 2);
        }
        catch (Exception ex)
        {
            G.LogError("Error calculating CPU usage", ex);
            return 0;
        }
    }

    private async Task PublishMetricAsync(MetricValue metric)
    {
        try
        {
            await _bus.PublishAsync($"ghost:metrics:{metric.Name}",
                    JsonSerializer.Serialize(new MetricReading
                    {
                            Name = metric.Name,
                            Value = metric.Value,
                            Tags = metric.Tags,
                            Timestamp = metric.Timestamp
                    }));
        }
        catch (Exception ex)
        {
            G.LogError("Error publishing metric", ex);
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
            await _metricsTimer.DisposeAsync();
            _metrics.Clear();
        }
        finally
        {
            var localLock = _lock;
            _lock = null;
            localLock.Release();
            localLock.Dispose();
        }
    }

    // System Event class for process communication
    public class SystemEvent
    {
        public string Type { get; set; }
        public string Source { get; set; }
        public string Data { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public T GetData<T>() where T : class
        {
            if (string.IsNullOrEmpty(Data)) return null;
            return JsonSerializer.Deserialize<T>(Data);
        }
    }

    // State Manager for persistence
    public class StateManager
    {
        private readonly IGhostData _data;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _initialized;

        public StateManager(IGhostData data, ILogger logger)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InitializeAsync()
        {
            if (_initialized) return;

            await _lock.WaitAsync();
            try
            {
                if (_initialized) return;

                // Ensure schema exists
                await _data.ExecuteAsync(@"
                CREATE TABLE IF NOT EXISTS processes (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    type TEXT NOT NULL,
                    version TEXT NOT NULL,
                    status TEXT NOT NULL,
                    metadata TEXT,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS process_metrics (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    process_id TEXT NOT NULL,
                    cpu_percentage REAL,
                    memory_bytes BIGINT,
                    thread_count INTEGER,
                    handle_count INTEGER,
                    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (process_id) REFERENCES processes(id)
                );

                CREATE INDEX IF NOT EXISTS idx_process_metrics_time 
                ON process_metrics(process_id, timestamp);
            ");

                _initialized = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveProcessAsync(ProcessInfo process)
        {
            await EnsureInitializedAsync();

            try
            {
                await using var transaction = await _data.BeginTransactionAsync();
                try
                {
                    // Save process info
                    await _data.ExecuteAsync(@"
                    INSERT INTO processes (
                        id, name, type, version, status, metadata, created_at, updated_at
                    ) VALUES (
                        @id, @name, @type, @version, @status, @metadata, @timestamp, @timestamp
                    )", new
                    {
                            id = process.Id,
                            name = process.Metadata.Name,
                            type = process.Metadata.Type,
                            version = process.Metadata.Version,
                            status = process.Status.ToString(),
                            metadata = JsonSerializer.Serialize(process.Metadata),
                            timestamp = DateTime.UtcNow
                    });

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                G.LogError("Failed to save process state: {Id}", ex, process.Id);
                throw;
            }
        }

        public async Task UpdateProcessStatusAsync(string processId, ProcessStatus status)
        {
            await EnsureInitializedAsync();

            try
            {
                await _data.ExecuteAsync(@"
                UPDATE processes 
                SET status = @status, updated_at = @timestamp
                WHERE id = @processId",
                        new
                        {
                                processId,
                                status = status.ToString(),
                                timestamp = DateTime.UtcNow
                        });
            }
            catch (Exception ex)
            {
                G.LogError("Failed to update process status: {Id} -> {Status}",
                        ex, processId, status);
                throw;
            }
        }

        public async Task UpdateProcessMetricsAsync(string processId, ProcessMetrics metrics)
        {
            await EnsureInitializedAsync();

            try
            {
                await using var transaction = await _data.BeginTransactionAsync();
                try
                {
                    // Save metrics
                    await _data.ExecuteAsync(@"
                    INSERT INTO process_metrics (
                        process_id, cpu_percentage, memory_bytes, 
                        thread_count, handle_count, timestamp
                    ) VALUES (
                        @processId, @cpuPercentage, @memoryBytes,
                        @threadCount, @handleCount, @timestamp
                    )", new
                    {
                            processId,
                            cpuPercentage = metrics.CpuPercentage,
                            memoryBytes = metrics.MemoryBytes,
                            threadCount = metrics.ThreadCount,
                            handleCount = metrics.HandleCount,
                            timestamp = metrics.Timestamp
                    });

                    // Cleanup old metrics
                    await CleanupOldMetricsAsync(processId);

                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                G.LogError("Failed to update process metrics: {Id}", ex, processId);
                throw;
            }
        }

        public async Task<IEnumerable<ProcessInfo>> GetActiveProcessesAsync()
        {
            await EnsureInitializedAsync();

            try
            {
                var processes = await _data.QueryAsync<ProcessInfo>(@"
                SELECT * FROM processes 
                WHERE status != @status",
                        new
                        {
                                status = ProcessStatus.Stopped
                        });

                return processes ?? Enumerable.Empty<ProcessInfo>();
            }
            catch (Exception ex)
            {
                G.LogError("Failed to get active processes", ex);
                throw;
            }
        }

        private async Task CleanupOldMetricsAsync(string processId)
        {
            // Keep last 24 hours of metrics
            var cutoff = DateTime.UtcNow.AddHours(-24);

            await _data.ExecuteAsync(@"
            DELETE FROM process_metrics
            WHERE process_id = @processId 
            AND timestamp < @cutoff",
                    new
                    {
                            processId,
                            cutoff
                    });
        }

        private async Task EnsureInitializedAsync()
        {
            if (!_initialized)
            {
                await InitializeAsync();
            }
        }
    }

// Metric reading record for storing metrics
    public record MetricReading
    {
        public string Name { get; init; }
        public double Value { get; init; }
        public Dictionary<string, string> Tags { get; init; }
        public DateTime Timestamp { get; init; }
    }

// Metric value class for internal tracking
    public class MetricValue
    {
        public string Name { get; set; }
        public double Value { get; set; }
        public Dictionary<string, string> Tags { get; set; }
        public DateTime Timestamp { get; set; }
    }
}