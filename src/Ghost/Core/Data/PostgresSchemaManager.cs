using System.Reflection;
namespace Ghost.Core.Data;

public class PostgresSchemaManager : ISchemaManager
{
    private readonly IDatabaseClient  _db;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public PostgresSchemaManager(IDatabaseClient  db)
    {
        _db = db;
    }

    public async Task InitializeAsync<T>() where T : class
    {
        var type = typeof(T);
        await InitializeAsync(type);
    }

    public async Task InitializeAsync(params Type[] types)
    {
        await _lock.WaitAsync();
        try
        {
            // Ensure public schema exists
            await _db.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS public");

            foreach (var type in types)
            {
                var schema = GenerateSchema(type);
                await _db.ExecuteAsync(schema);
            }
            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var tables = await _db.GetTableNamesAsync();
            foreach (var table in tables)
            {
                await _db.ExecuteAsync($"DROP TABLE IF EXISTS {table} CASCADE");
            }
            _initialized = false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string GenerateSchema(Type type)
    {
        var tableName = type.Name.ToLowerInvariant();
        var columns = new List<string>();
        var indices = new List<string>();
        var constraints = new List<string>();

        foreach (var prop in type.GetProperties())
        {
            var columnName = prop.Name.ToLowerInvariant();
            var columnType = GetPostgresType(prop);
            var columnConstraints = new List<string>();

            // Handle primary key
            if (IsPrimaryKey(prop))
            {
                if (prop.PropertyType == typeof(int))
                {
                    columnType = "SERIAL";
                }
                else if (prop.PropertyType == typeof(long))
                {
                    columnType = "BIGSERIAL";
                }
                columnConstraints.Add("PRIMARY KEY");
            }

            // Handle required/not null
            if (IsRequired(prop))
            {
                columnConstraints.Add("NOT NULL");
            }

            // Handle index
            if (ShouldIndex(prop))
            {
                indices.Add($"CREATE INDEX IF NOT EXISTS idx_{tableName}_{columnName} ON {tableName} USING btree ({columnName});");
            }

            columns.Add($"{columnName} {columnType} {string.Join(" ", columnConstraints)}".TrimEnd());
        }

        return $@"
            CREATE TABLE IF NOT EXISTS {tableName} (
                {string.Join(",\n    ", columns)}
                {(constraints.Any() ? "," : "")}
                {string.Join(",\n    ", constraints)}
            );
            {string.Join("\n", indices)}
        ";
    }

    private static string GetPostgresType(PropertyInfo prop)
    {
        var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

        return type switch
        {
            Type t when t == typeof(int) => "INTEGER",
            Type t when t == typeof(long) => "BIGINT",
            Type t when t == typeof(string) => "TEXT",
            Type t when t == typeof(DateTime) => "TIMESTAMP WITH TIME ZONE",
            Type t when t == typeof(bool) => "BOOLEAN",
            Type t when t == typeof(decimal) => "NUMERIC",
            Type t when t == typeof(double) => "DOUBLE PRECISION",
            Type t when t == typeof(float) => "REAL",
            Type t when t == typeof(Guid) => "UUID",
            Type t when t == typeof(byte[]) => "BYTEA",
            Type t when t.IsEnum => "INTEGER",
            _ => "JSONB" // Use JSONB for complex types
        };
    }

    private static bool IsPrimaryKey(PropertyInfo prop)
    {
        return prop.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.KeyAttribute), true).Any()
            || prop.Name.Equals("Id", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRequired(PropertyInfo prop)
    {
        return prop.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RequiredAttribute), true).Any()
            || (prop.PropertyType.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) == null);
    }

    private static bool ShouldIndex(PropertyInfo prop)
    {
        return false;
        // return prop.GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.Schema.IndexAttribute), true).Any()
        //     || IsPrimaryKey(prop)
        //     || prop.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase);
    }
    public Task InitializeAsync<T>(CancellationToken ct = default) where T : class
    {
        throw new NotImplementedException();
    }
    public Task InitializeAsync(Type[] types, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
    public Task<bool> ExistsAsync<T>(CancellationToken ct = default) where T : class
    {
        throw new NotImplementedException();
    }
    public Task<bool> ValidateAsync<T>(CancellationToken ct = default) where T : class
    {
        throw new NotImplementedException();
    }
    public Task MigrateAsync<T>(CancellationToken ct = default) where T : class
    {
        throw new NotImplementedException();
    }
    public Task ResetAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
    public Task<IEnumerable<string>> GetTablesAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
    public Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
    public Task<IEnumerable<IndexInfo>> GetIndexesAsync(string tableName, CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}