using Ghost.Core.Monitoring;
using Ghost.Core.PM;
using Ghost.Core.Storage;
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
                    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (process_id) REFERENCES processes(id)
                );

                CREATE INDEX IF NOT EXISTS idx_process_metrics_time 
                ON process_metrics(process_id, timestamp);");

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
                    )",
                    new
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
            G.LogError(ex, "Failed to save process state: {Id}", process.Id);
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
            G.LogError(
                ex, 
                "Failed to update process status: {Id} -> {Status}", 
                processId, 
                status);
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
                        process_id,
                        cpu_percentage,
                        memory_bytes,
                        thread_count,
                        handle_count,
                        timestamp
                    ) VALUES (
                        @processId,
                        @cpuPercentage,
                        @memoryBytes,
                        @threadCount,
                        @handleCount,
                        @timestamp
                    )",
                    new
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
            G.LogError(ex, "Failed to update process metrics: {Id}", processId);
            throw;
        }
    }

    public async Task<IEnumerable<ProcessMetrics>> GetProcessMetricsAsync(
        string processId,
        DateTime start,
        DateTime end,
        int? limit = null)
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

            return await _data.QueryAsync<ProcessMetrics>(
                sql,
                new { processId, start, end, limit });
        }
        catch (Exception ex)
        {
            G.LogError(
                ex, 
                "Failed to get process metrics: {Id} ({Start} -> {End})", 
                processId, 
                start, 
                end);
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
    public async Task<IEnumerable<ProcessInfo>> GetActiveProcessesAsync()
    {
        return await _data.QueryAsync<ProcessInfo>(
            "SELECT * FROM processes WHERE status = 'running'");
    }
    public async Task<ProcessInfo> GetProcessStatusAsync(string? processId)
    {
        if (string.IsNullOrWhiteSpace(processId))
        {
            return null;
        }

        return await _data.QuerySingleAsync<ProcessInfo>(
            "SELECT * FROM processes WHERE id = @processId",
            new { processId });
    }
}