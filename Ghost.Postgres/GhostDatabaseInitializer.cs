using Ghost.Core.Monitoring;
using Ghost.Core.Monitoring;

namespace Ghost.Core.Data;

public class GhostDatabaseInitializer
{
  private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
  private readonly GhostDataOptions _options;
  private readonly ISchemaManager _schemaManager;
  private bool _initialized;

  public GhostDatabaseInitializer(ISchemaManager schemaManager, GhostDataOptions options)
  {
    _schemaManager = schemaManager;
    _options = options;
  }

  public async Task InitializeAsync(CancellationToken ct = default(CancellationToken))
  {
    if (_initialized) return;

    await _lock.WaitAsync(ct);
    try
    {
      if (_initialized) return;

      L.LogInfo("Initializing Ghost database schemas...");

      // Core system tables
      var systemTypes = new[]
      {
          // Storage
          typeof(KeyValueStore),

          // Process Management
          typeof(ProcessInfo),
          typeof(ProcessMetrics),
          typeof(ProcessState),

          // Configuration
          typeof(ConfigEntry), typeof(StateEntry),

          // Logging
          typeof(LogEntry)
      };

      await _schemaManager.InitializeAsync(systemTypes, ct);
      L.LogInfo("Core system schemas initialized");

      // Custom entity tables from configuration
      if (_options.EntityTypes.Length != 0)
      {
        await _schemaManager.InitializeAsync(_options.EntityTypes, ct);
        L.LogInfo($"Custom entity schemas initialized: {_options.EntityTypes.Length} types");
      }

      _initialized = true;
      L.LogInfo("Ghost database initialization completed successfully");
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Failed to initialize Ghost database schemas");
      throw;
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task ValidateAsync(CancellationToken ct = default(CancellationToken))
  {
    await _lock.WaitAsync(ct);
    try
    {
      L.LogInfo("Validating Ghost database schemas...");

      var tables = await _schemaManager.GetTablesAsync(ct);
      L.LogInfo($"Found {tables.Count()} tables in database");

      foreach (string table in tables)
      {
        var columns = await _schemaManager.GetColumnsAsync(table, ct);
        var indices = await _schemaManager.GetIndexesAsync(table, ct);

        L.LogDebug($"Table {table}: {columns.Count()} columns, {indices.Count()} indices");
      }

      L.LogInfo("Ghost database validation completed");
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Failed to validate Ghost database schemas");
      throw;
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task ResetAsync(CancellationToken ct = default(CancellationToken))
  {
    await _lock.WaitAsync(ct);
    try
    {
      L.LogWarn("Resetting Ghost database schemas...");

      await _schemaManager.ResetAsync(ct);
      _initialized = false;

      L.LogInfo("Ghost database reset completed");
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Failed to reset Ghost database schemas");
      throw;
    }
    finally
    {
      _lock.Release();
    }
  }
}
