using Dapper;
using Microsoft.Data.Sqlite;
using System.Data.Common;
namespace Ghost.Legacy.Infrastructure.Database;

public class SqliteProvider : IDbProvider
{
  private readonly string _dbPath;
  private readonly string _tablePrefix;

  public SqliteProvider(string? dbPath = null, string tablePrefix = "ghost_")
  {
    _dbPath = dbPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ghost",
        "ghost.db"
    );
    _tablePrefix = tablePrefix;
  }

  public DbConnection CreateConnection()
  {
    var builder = new SqliteConnectionStringBuilder
    {
        DataSource = _dbPath,
        Cache = SqliteCacheMode.Shared,
        Mode = SqliteOpenMode.ReadWriteCreate
    };

    return new SqliteConnection(builder.ConnectionString);
  }

  public void Initialize()
  {
    Directory.CreateDirectory(Path.GetDirectoryName(_dbPath));
    using var conn = CreateConnection();
    conn.Open();

    // Create tables with the specified prefix
    conn.Execute($@"
            CREATE TABLE IF NOT EXISTS {_tablePrefix}processes (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                status TEXT NOT NULL,
                pid INTEGER,
                port INTEGER,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS {_tablePrefix}config (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                app_id TEXT NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS {_tablePrefix}events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                app_id TEXT NOT NULL,
                payload TEXT,
                processed INTEGER DEFAULT 0,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS idx_{_tablePrefix}events_processed 
            ON {_tablePrefix}events(processed, created_at);
        ");
  }

  public string GetTablePrefix() => _tablePrefix;
}
