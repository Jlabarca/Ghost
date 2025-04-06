using Ghost.Core.Config;
using Ghost.Core.Data;
using System.Data;
namespace Ghost.SDK;

/// <summary>
/// Manages access to data storage
/// </summary>
public class DataManager
{
  private readonly IGhostData _data;

  public DataManager(GhostConfig config)
  {
    // Initialize data provider based on config
    string dataPath = config.Core.DataPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ghost",
        "data");

    string dbPath = Path.Combine(dataPath, "ghost.db");
    Directory.CreateDirectory(dataPath);

    var db = Services.GetRequiredService<IDatabaseClient>();
    var kvStore = new SQLiteKeyValueStore(db);
    var cache = new LocalCache(config.GetModuleConfig<LocalCacheConfig>("cache")?.Path ?? "cache");

    var schema = db.DatabaseType == DatabaseType.PostgreSQL
        ? new PostgresSchemaManager(db)
        : new SQLiteSchemaManager(db) as ISchemaManager;
    _data = new GhostData(db, kvStore, cache, schema);
    _data.InitializeAsync().GetAwaiter().GetResult();
  }

  /// <summary>
  /// Get the raw data access object
  /// </summary>
  public IGhostData Data => _data;

  /// <summary>
  /// Execute a SQL command
  /// </summary>
  public async Task<int> ExecuteAsync(string sql, object param = null)
  {
    return await _data.ExecuteAsync(sql, param);
  }

  /// <summary>
  /// Query data with a SQL command
  /// </summary>
  public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
  {
    return await _data.QueryAsync<T>(sql, param);
  }

  /// <summary>
  /// Query a single result with a SQL command
  /// </summary>
  public async Task<T> QuerySingleAsync<T>(string sql, object param = null)
  {
    return await _data.QuerySingleAsync<T>(sql, param);
  }

  /// <summary>
  /// Begin a transaction
  /// </summary>
  public async Task<IDbTransaction> BeginTransactionAsync()
  {
    return await _data.BeginTransactionAsync();
  }
}
