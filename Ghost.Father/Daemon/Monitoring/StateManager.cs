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

      // We don't need to create tables - they're already created by init-db.sql and init-monitoring.sql
      // Just verify that tables exist
      bool processesTableExists = await _data.TableExistsAsync("processes");
      bool processEventsTableExists = await _data.TableExistsAsync("process_events");

      if (!processesTableExists || !processEventsTableExists)
      {
        throw new InvalidOperationException(
            "Required database tables are missing. Please ensure the database was properly initialized with the init-db.sql script.");
      }

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

        // Save process info - using PostgreSQL syntax for upsert
        // Note: Use @config::jsonb to properly cast the JSON string to jsonb type
        await _data.ExecuteAsync(@"
                    INSERT INTO processes (
                        id, name, type, status, config, last_heartbeat, created_at, updated_at
                    ) VALUES (
                        @id, @name, @type, @status, @config::jsonb, @timestamp, @timestamp, @timestamp
                    ) ON CONFLICT (id) DO UPDATE SET
                        name = @name,
                        type = @type,
                        status = @status,
                        config = @config::jsonb,
                        updated_at = @timestamp",
            new
            {
                id = process.Id,
                name = process.Metadata.Name,
                type = appType,
                status = process.Status.ToString(),
                config = JsonSerializer.Serialize(process.Metadata), // Use config field from new schema
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
      G.LogError(ex, "Failed to save process state: {0}", process.Id);
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
      G.LogError(ex, $"Failed to update process status: {processId} -> {status}");
      throw;
    }
  }

  public async Task SaveProcessMetricsAsync(string processId, ProcessMetrics metrics, string appType)
  {
    await EnsureInitializedAsync();
    try
    {
      // Store metrics in the process_events table rather than creating a separate table
      await using var transaction = await _data.BeginTransactionAsync();
      try
      {
        // Create a metrics event
        var metricsData = new
        {
            cpu_percentage = metrics.CpuPercentage,
            memory_bytes = metrics.MemoryBytes,
            thread_count = metrics.ThreadCount,
            handle_count = metrics.HandleCount,
            timestamp = metrics.Timestamp
        };

        byte[] serializedMetrics = System.Text.Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(metricsData));

        // Save metrics as an event
        await _data.ExecuteAsync(@"
                    INSERT INTO process_events (
                        process_id, event_type, event_data, timestamp
                    ) VALUES (
                        @processId, @eventType, @eventData, @timestamp
                    )", new
        {
            processId,
            eventType = "metrics",
            eventData = serializedMetrics,
            timestamp = metrics.Timestamp
        });

        // Also update the metrics JSON in the processes table
        await _data.ExecuteAsync(@"
                    UPDATE processes 
                    SET metrics = jsonb_set(
                        COALESCE(metrics, '{}'::jsonb),
                        '{latest}',
                        @metrics::jsonb
                    ),
                    last_heartbeat = @timestamp
                    WHERE id = @processId",
            new
            {
                processId,
                metrics = JsonSerializer.Serialize(metricsData),
                timestamp = metrics.Timestamp
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
      G.LogError(ex, "Failed to save process metrics: {0}", processId);
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
                    (event_data->>'cpu_percentage')::float as CpuPercentage,
                    (event_data->>'memory_bytes')::bigint as MemoryBytes,
                    (event_data->>'thread_count')::int as ThreadCount,
                    (event_data->>'handle_count')::int as HandleCount,
                    timestamp as Timestamp
                FROM process_events
                WHERE process_id = @processId
                    AND event_type = 'metrics'
                    AND timestamp BETWEEN @start AND @end
                ORDER BY timestamp DESC";

      if (limit.HasValue)
      {
        sql += " LIMIT @limit";
      }

      // Query will need modification since we're now storing metrics as JSON in event_data
      return await _data.QueryAsync<ProcessMetrics>(sql, new
      {
          processId,
          start,
          end,
          limit
      });
    }
    catch (Exception ex)
    {
      G.LogError(ex, $"Failed to get process metrics: {processId} ({start} -> {end})");
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
      G.LogError(ex, "Failed to get active processes");
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
          new
          {
              processId
          });
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to get process: {0}", processId);
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
                        p.metrics->'latest'->>'cpu_percentage' as CpuPercentage,
                        p.metrics->'latest'->>'memory_bytes' as MemoryBytes
                    FROM processes p
                    ORDER BY p.updated_at DESC");
        return new
        {
            Processes = processes
        };
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
                    p.config
                FROM processes p
                WHERE p.id = @processId",
          new
          {
              processId
          });

      if (process == null)
      {
        return null;
      }

      // Get recent metrics from process_events
      var metrics = await _data.QueryAsync<dynamic>(@"
                SELECT 
                    (event_data->>'cpu_percentage')::float as CpuPercentage,
                    (event_data->>'memory_bytes')::bigint as MemoryBytes,
                    (event_data->>'thread_count')::int as ThreadCount,
                    (event_data->>'handle_count')::int as HandleCount,
                    timestamp as Timestamp
                FROM process_events
                WHERE process_id = @processId
                  AND event_type = 'metrics'
                ORDER BY timestamp DESC
                LIMIT 5",
          new
          {
              processId
          });

      return new
      {
          Process = process,
          Metrics = metrics
      };
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to get process status: {0}", processId);
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
            new
            {
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
      G.LogError(ex, "Failed to persist process state");
      throw;
    }
  }

  private async Task EnsureInitializedAsync()
  {
    if (!_initialized)
    {
      await InitializeAsync();
    }
  }
}
