// using Dapper;
// using System.Data.Common;
//
// namespace Ghost.Infrastructure.Database;
//
// public class PostgresProvider : IDbProvider
// {
//   private readonly string _connectionString;
//   private readonly string _tablePrefix;
//
//   public PostgresProvider(string connectionString, string tablePrefix = "ghost_")
//   {
//     _connectionString = connectionString;
//     _tablePrefix = tablePrefix;
//   }
//
//   public DbConnection CreateConnection()
//     => new NpgsqlConnection(_connectionString);
//
//   public void Initialize()
//   {
//     using var conn = CreateConnection();
//     conn.Open();
//
//     // Create tables with proper PostgreSQL syntax
//     conn.Execute($@"
//             CREATE TABLE IF NOT EXISTS {_tablePrefix}processes (
//                 id TEXT PRIMARY KEY,
//                 name TEXT NOT NULL,
//                 status TEXT NOT NULL,
//                 pid INTEGER,
//                 port INTEGER,
//                 created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
//                 updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
//             );
//             -- ... similar for other tables
//         ");
//   }
//
//   public string GetTablePrefix() => _tablePrefix;
// }
