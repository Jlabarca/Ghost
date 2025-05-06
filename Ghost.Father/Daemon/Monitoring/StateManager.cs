using Ghost.Core;
using Ghost.Core.Data;
using Ghost.Core.Monitoring;
using System.Text.Json;

namespace Ghost.Father;

/// <summary>
/// Manages persistence of process state and metrics
/// </summary>
public class StateManager
{
    private readonly IGhostData _data;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public StateManager(IGhostData data)
    {
        _data = data;
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
                    app_type TEXT NOT NULL,
                    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (process_id) REFERENCES processes(id)
                );
                
                CREATE INDEX IF NOT EXISTS idx_process_metrics_time ON process_metrics(process_id, timestamp);");

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
                // Get the app type from metadata
                var appType = (process.Metadata.Configuration.TryGetValue("AppType", out var type)
                              ? type
                              : "one-shot").ToLowerInvariant();

                // Save process info
                await _data.ExecuteAsync(@"
                    INSERT OR REPLACE INTO processes (
                        id, name, type, version, status, metadata, created_at, updated_at
                    ) VALUES (
                        @id, @name, @type, @version, @status, @metadata, @timestamp, @timestamp
                    )", new
                    {
                        id = process.Id,
                        name = process.Metadata.Name,
                        type = appType,
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
            L.LogError(ex, "Failed to save process state: {Id}", process.Id);
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
            L.LogError(ex, "Failed to update process status: {Id} -> {Status}", processId, status);
            throw;
        }
    }

    public async Task SaveProcessMetricsAsync(string processId, ProcessMetrics metrics, string appType)
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
                        process_id, cpu_percentage, memory_bytes, thread_count, 
                        handle_count, app_type, timestamp
                    ) VALUES (
                        @processId, @cpuPercentage, @memoryBytes, @threadCount,
                        @handleCount, @appType, @timestamp
                    )", new
                    {
                        processId,
                        cpuPercentage = metrics.CpuPercentage,
                        memoryBytes = metrics.MemoryBytes,
                        threadCount = metrics.ThreadCount,
                        handleCount = metrics.HandleCount,
                        appType = appType ?? "one-shot",
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
            L.LogError(ex, "Failed to save process metrics: {Id}", processId);
            throw;
        }
    }

    public async Task<IEnumerable<ProcessMetrics>> GetProcessMetricsAsync(
        string processId, DateTime start, DateTime end, int? limit = null)
    {
        await EnsureInitializedAsync();

        try
        {
            var sql = @"
                SELECT 
                    process_id as ProcessId,
                    cpu_percentage as CpuPercentage,
                    memory_bytes as MemoryBytes,
                    thread_count as ThreadCount,
                    handle_count as HandleCount,
                    timestamp as Timestamp
                FROM process_metrics
                WHERE process_id = @processId
                  AND timestamp BETWEEN @start AND @end
                ORDER BY timestamp DESC";

            if (limit.HasValue)
            {
                sql += " LIMIT @limit";
            }

            return await _data.QueryAsync<ProcessMetrics>(sql, new { processId, start, end, limit });
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to get process metrics: {Id} ({Start} -> {End})", processId, start, end);
            throw;
        }
    }

    public async Task<IEnumerable<ProcessInfo>> GetActiveProcessesAsync()
    {
        await EnsureInitializedAsync();

        try
        {
            // Get list of active processes
            var processes = await _data.QueryAsync<ProcessInfo>(@"
                SELECT * FROM processes 
                WHERE status = 'Running' OR status = 'Starting'");

            return processes;
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to get active processes");
            throw;
        }
    }

    public async Task<ProcessInfo> GetProcessAsync(string processId)
    {
        await EnsureInitializedAsync();

        try
        {
            if (string.IsNullOrEmpty(processId))
            {
                return null;
            }

            return await _data.QuerySingleAsync<ProcessInfo>(@"
                SELECT * FROM processes
                WHERE id = @processId",
                new { processId });
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to get process: {Id}", processId);
            throw;
        }
    }

    public async Task<dynamic> GetProcessStatusAsync(string? processId)
    {
        await EnsureInitializedAsync();

        try
        {
            if (string.IsNullOrEmpty(processId))
            {
                // Return all processes
                var processes = await _data.QueryAsync<dynamic>(@"
                    SELECT 
                        p.id,
                        p.name,
                        p.type,
                        p.status,
                        p.created_at as StartTime,
                        p.updated_at as LastUpdate,
                        MAX(m.cpu_percentage) as CpuPercentage,
                        MAX(m.memory_bytes) as MemoryBytes
                    FROM processes p
                    LEFT JOIN process_metrics m ON p.id = m.process_id
                    GROUP BY p.id, p.name, p.type, p.status, p.created_at, p.updated_at
                    ORDER BY p.updated_at DESC");

                return new { Processes = processes };
            }

            // Get specific process with its latest metrics
            var process = await _data.QuerySingleAsync<dynamic>(@"
                SELECT 
                    p.id,
                    p.name,
                    p.type,
                    p.status,
                    p.created_at as StartTime,
                    p.updated_at as LastUpdate,
                    p.metadata
                FROM processes p
                WHERE p.id = @processId",
                new { processId });

            if (process == null)
            {
                return null;
            }

            // Get the 5 most recent metrics
            var metrics = await _data.QueryAsync<dynamic>(@"
                SELECT 
                    cpu_percentage as CpuPercentage,
                    memory_bytes as MemoryBytes,
                    thread_count as ThreadCount,
                    handle_count as HandleCount,
                    app_type as AppType,
                    timestamp as Timestamp
                FROM process_metrics
                WHERE process_id = @processId
                ORDER BY timestamp DESC
                LIMIT 5",
                new { processId });

            return new { Process = process, Metrics = metrics };
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to get process status: {Id}", processId);
            throw;
        }
    }

    public async Task PersistStateAsync()
    {
        await EnsureInitializedAsync();

        try
        {
            await using var transaction = await _data.BeginTransactionAsync();
            try
            {
                // Update all running processes to 'stopped' status
                await _data.ExecuteAsync(@"
                    UPDATE processes
                    SET status = 'Stopped', updated_at = @timestamp
                    WHERE status = 'Running'",
                    new { timestamp = DateTime.UtcNow });

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
            L.LogError(ex, "Failed to persist process state");
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
            new { processId, cutoff });
    }

    private async Task EnsureInitializedAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }
    }
}