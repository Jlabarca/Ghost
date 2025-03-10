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

      G.LogInfo("Initializing Ghost database schemas...");

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
      G.LogInfo("Core system schemas initialized");

      // Custom entity tables from configuration
      if (_options.EntityTypes.Length != 0)
      {
        await _schemaManager.InitializeAsync(_options.EntityTypes, ct);
        G.LogInfo($"Custom entity schemas initialized: {_options.EntityTypes.Length} types");
      }

      _initialized = true;
      G.LogInfo("Ghost database initialization completed successfully");
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to initialize Ghost database schemas");
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
      G.LogInfo("Validating Ghost database schemas...");

      var tables = await _schemaManager.GetTablesAsync(ct);
      G.LogInfo($"Found {tables.Count()} tables in database");

      foreach (string table in tables)
      {
        var columns = await _schemaManager.GetColumnsAsync(table, ct);
        var indices = await _schemaManager.GetIndexesAsync(table, ct);

        G.LogDebug($"Table {table}: {columns.Count()} columns, {indices.Count()} indices");
      }

      G.LogInfo("Ghost database validation completed");
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to validate Ghost database schemas");
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
      G.LogWarn("Resetting Ghost database schemas...");

      await _schemaManager.ResetAsync(ct);
      _initialized = false;

      G.LogInfo("Ghost database reset completed");
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to reset Ghost database schemas");
      throw;
    }
    finally
    {
      _lock.Release();
    }
  }
}
