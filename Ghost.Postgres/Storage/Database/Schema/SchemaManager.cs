// using Ghost.Core.Data;
// using System.ComponentModel.DataAnnotations;
// using System.Reflection;
// namespace Ghost.Core.Storage;
//
//
// public class SqliteSchemaManager : ISchemaManager
// {
//     private readonly IDatabaseClient _db;
//
//     public SqliteSchemaManager(IDatabaseClient db)
//     {
//         _db = db;
//     }
//
//     public async Task InitializeAsync<T>() where T : class
//     {
//         await InitializeAsync(typeof(T));
//     }
//
//     public async Task InitializeAsync(params Type[] types)
//     {
//         await using var tx = await _db.BeginTransactionAsync();
//         try
//         {
//             foreach (var type in types)
//             {
//                 var tableName = type.Name.ToLowerInvariant();
//                 var schema = BuildSchema(type);
//                 await _db.ExecuteAsync(schema);
//             }
//             await tx.CommitAsync();
//         }
//         catch
//         {
//             await tx.RollbackAsync();
//             throw;
//         }
//     }
//
//     public async Task ResetAsync()
//     {
//         var tables = await _db.QueryAsync<string>(
//             "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
//
//         await using var tx = await _db.BeginTransactionAsync();
//         try
//         {
//             foreach (var table in tables)
//             {
//                 await _db.ExecuteAsync($"DROP TABLE IF EXISTS {table}");
//             }
//             await tx.CommitAsync();
//         }
//         catch
//         {
//             await tx.RollbackAsync();
//             throw;
//         }
//     }
//
//     private static string BuildSchema(Type type)
//     {
//         var tableName = type.Name.ToLowerInvariant();
//         var columns = new List<string>();
//         var indices = new List<string>();
//
//         foreach (var prop in type.GetProperties())
//         {
//             if (prop.GetCustomAttribute<NotMappedAttribute>() != null)
//                 continue;
//
//             var columnName = prop.Name.ToLowerInvariant();
//             var columnType = GetSqliteType(prop.PropertyType);
//             var constraints = new List<string>();
//
//             // Primary key
//             var keyAttr = prop.GetCustomAttribute<KeyAttribute>();
//             if (keyAttr != null || prop.Name.Equals("Id", StringComparison.OrdinalIgnoreCase))
//             {
//                 constraints.Add("PRIMARY KEY");
//                 if (prop.PropertyType == typeof(int))
//                     constraints.Add("AUTOINCREMENT");
//             }
//
//             // Required/NotNull
//             var isRequired = prop.GetCustomAttribute<RequiredAttribute>() != null;
//             if (isRequired)
//                 constraints.Add("NOT NULL");
//
//             // Indexed
//             var indexAttr = prop.GetCustomAttribute<IndexedAttribute>();
//             if (indexAttr != null)
//             {
//                 indices.Add($"CREATE INDEX IF NOT EXISTS idx_{tableName}_{columnName} ON {tableName}({columnName});");
//             }
//
//             columns.Add($"{columnName} {columnType} {string.Join(" ", constraints)}".TrimEnd());
//         }
//
//         var schema = $@"
// CREATE TABLE IF NOT EXISTS {tableName} (
//     {string.Join(",\n    ", columns)}
// );
//
// {string.Join("\n", indices)}";
//
//         return schema;
//     }
//
//     private static string GetSqliteType(Type type)
//     {
//         type = Nullable.GetUnderlyingType(type) ?? type;
//
//         return type switch
//         {
//             Type t when t == typeof(int) => "INTEGER",
//             Type t when t == typeof(long) => "INTEGER",
//             Type t when t == typeof(string) => "TEXT",
//             Type t when t == typeof(DateTime) => "TEXT",
//             Type t when t == typeof(bool) => "INTEGER",
//             Type t when t == typeof(decimal) => "NUMERIC",
//             Type t when t == typeof(double) => "REAL",
//             Type t when t == typeof(float) => "REAL",
//             Type t when t == typeof(Guid) => "TEXT",
//             Type t when t == typeof(byte[]) => "BLOB",
//             Type t when t.IsEnum => "INTEGER",
//             _ => "TEXT"
//         };
//     }
// }
//
// public class PostgresSchemaManager : ISchemaManager
// {
//     private readonly IDatabaseClient _db;
//
//     public PostgresSchemaManager(IDatabaseClient db)
//     {
//         _db = db;
//     }
//
//     public async Task InitializeAsync<T>() where T : class
//     {
//         await InitializeAsync(typeof(T));
//     }
//
//     public async Task InitializeAsync(params Type[] types)
//     {
//         await using var tx = await _db.BeginTransactionAsync();
//         try
//         {
//             // Ensure public schema exists
//             await _db.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS public;");
//
//             foreach (var type in types)
//             {
//                 var tableName = type.Name.ToLowerInvariant();
//                 var schema = BuildSchema(type);
//                 await _db.ExecuteAsync(schema);
//             }
//             await tx.CommitAsync();
//         }
//         catch
//         {
//             await tx.RollbackAsync();
//             throw;
//         }
//     }
//
//     public async Task ResetAsync()
//     {
//         var tables = await _db.QueryAsync<string>(@"
//             SELECT table_name
//             FROM information_schema.tables
//             WHERE table_schema = 'public'");
//
//         await using var tx = await _db.BeginTransactionAsync();
//         try
//         {
//             foreach (var table in tables)
//             {
//                 await _db.ExecuteAsync($"DROP TABLE IF EXISTS public.{table} CASCADE");
//             }
//             await tx.CommitAsync();
//         }
//         catch
//         {
//             await tx.RollbackAsync();
//             throw;
//         }
//     }
//
//     private static string BuildSchema(Type type)
//     {
//         var tableName = type.Name.ToLowerInvariant();
//         var columns = new List<string>();
//         var indices = new List<string>();
//
//         foreach (var prop in type.GetProperties())
//         {
//             if (prop.GetCustomAttribute<NotMappedAttribute>() != null)
//                 continue;
//
//             var columnName = prop.Name.ToLowerInvariant();
//             var columnType = GetPostgresType(prop.PropertyType);
//             var constraints = new List<string>();
//
//             // Primary key
//             var keyAttr = prop.GetCustomAttribute<KeyAttribute>();
//             if (keyAttr != null || prop.Name.Equals("Id", StringComparison.OrdinalIgnoreCase))
//             {
//                 constraints.Add("PRIMARY KEY");
//                 if (prop.PropertyType == typeof(int))
//                 {
//                     columnType = "SERIAL";
//                 }
//                 else if (prop.PropertyType == typeof(long))
//                 {
//                     columnType = "BIGSERIAL";
//                 }
//             }
//
//             // Required/NotNull
//             var isRequired = prop.GetCustomAttribute<RequiredAttribute>() != null;
//             if (isRequired)
//                 constraints.Add("NOT NULL");
//
//             // Indexed
//             var indexAttr = prop.GetCustomAttribute<IndexedAttribute>();
//             if (indexAttr != null)
//             {
//                 indices.Add($"CREATE INDEX IF NOT EXISTS idx_{tableName}_{columnName} ON public.{tableName} USING btree ({columnName});");
//             }
//
//             columns.Add($"{columnName} {columnType} {string.Join(" ", constraints)}".TrimEnd());
//         }
//
//         var schema = $@"
// CREATE TABLE IF NOT EXISTS public.{tableName} (
//     {string.Join(",\n    ", columns)}
// );
//
// {string.Join("\n", indices)}";
//
//         return schema;
//     }
//
//     private static string GetPostgresType(Type type)
//     {
//         type = Nullable.GetUnderlyingType(type) ?? type;
//
//         return type switch
//         {
//             Type t when t == typeof(int) => "INTEGER",
//             Type t when t == typeof(long) => "BIGINT",
//             Type t when t == typeof(string) => "TEXT",
//             Type t when t == typeof(DateTime) => "TIMESTAMP WITH TIME ZONE",
//             Type t when t == typeof(bool) => "BOOLEAN",
//             Type t when t == typeof(decimal) => "NUMERIC",
//             Type t when t == typeof(double) => "DOUBLE PRECISION",
//             Type t when t == typeof(float) => "REAL",
//             Type t when t == typeof(Guid) => "UUID",
//             Type t when t == typeof(byte[]) => "BYTEA",
//             Type t when t.IsEnum => "INTEGER",
//             _ => "JSONB"  // Use JSONB for complex types in PostgreSQL
//         };
//     }
// }
//
// /// <summary>
// /// Marks a property as indexed in SQLite
// /// </summary>
// [AttributeUsage(AttributeTargets.Property)]
// public class IndexedAttribute : Attribute { }
//
// /// <summary>
// /// Marks a property as not mapped to database
// /// </summary>
// [AttributeUsage(AttributeTargets.Property)]
// public class NotMappedAttribute : Attribute { }