using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Ghost.Core.Data;

public class SQLiteSchemaManager : ISchemaManager
{
  private readonly IDatabaseClient _db;
  private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
  private bool _initialized;

  public SQLiteSchemaManager(IDatabaseClient db)
  {
    _db = db ?? throw new ArgumentNullException(nameof(db));
  }

  public async Task InitializeAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class
  {
    await InitializeAsync(new[]
    {
        typeof(T)
    }, ct);
  }

  public async Task InitializeAsync(Type[] types, CancellationToken ct = default(CancellationToken))
  {
    await _lock.WaitAsync(ct);
    try
    {
      foreach (Type type in types)
      {
        string schema = GenerateSchema(type);
        await _db.ExecuteAsync(schema);
      }
      _initialized = true;
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task<bool> ExistsAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class
  {
    string tableName = typeof(T).Name.ToLowerInvariant();
    int result = await _db.QuerySingleAsync<int>(
        "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
        new
        {
            name = tableName
        }
    );
    return result == 1;
  }

  public async Task<bool> ValidateAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class
  {
    Type type = typeof(T);
    string tableName = type.Name.ToLowerInvariant();

    // Get existing columns
    var existingColumns = await GetColumnsAsync(tableName, ct);
    var existingColumnDict = existingColumns.ToDictionary(c => c.Name.ToLower());

    // Get expected columns
    var properties = type.GetProperties()
        .Where(p => !p.GetCustomAttribute<NotMappedAttribute>()?.GetType().Name.Contains("NotMapped") ?? true);

    foreach (PropertyInfo prop in properties)
    {
      string columnName = prop.Name.ToLowerInvariant();
      if (!existingColumnDict.ContainsKey(columnName))
      {
        return false;
      }

      ColumnInfo column = existingColumnDict[columnName];
      string expectedType = GetSQLiteType(prop);
      if (!column.Type.Equals(expectedType, StringComparison.OrdinalIgnoreCase))
      {
        return false;
      }
    }

    return true;
  }

  public async Task MigrateAsync<T>(CancellationToken ct = default(CancellationToken)) where T : class
  {
    Type type = typeof(T);
    string tableName = type.Name.ToLowerInvariant();

    // Check if table exists
    if (!await ExistsAsync<T>(ct))
    {
      await InitializeAsync<T>(ct);
      return;
    }

    // Get existing columns
    var existingColumns = await GetColumnsAsync(tableName, ct);
    var existingColumnDict = existingColumns.ToDictionary(c => c.Name.ToLower());

    // Get properties to add
    var properties = type.GetProperties()
        .Where(p => !p.GetCustomAttribute<NotMappedAttribute>()?.GetType().Name.Contains("NotMapped") ?? true);

    await using var transaction = await _db.BeginTransactionAsync(ct);

    try
    {
      foreach (PropertyInfo prop in properties)
      {
        string columnName = prop.Name.ToLowerInvariant();
        if (!existingColumnDict.ContainsKey(columnName))
        {
          string columnType = GetSQLiteType(prop);
          string nullable = IsNullable(prop) ? "" : "NOT NULL";
          await _db.ExecuteAsync(
              $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnType} {nullable}", ct: ct);
        }
      }

      await transaction.CommitAsync();
    }
    catch
    {
      await transaction.RollbackAsync();
      throw;
    }
  }

  public async Task ResetAsync(CancellationToken ct = default(CancellationToken))
  {
    await _lock.WaitAsync(ct);
    try
    {
      var tables = await GetTablesAsync(ct);
      foreach (string table in tables)
      {
        await _db.ExecuteAsync($"DROP TABLE IF EXISTS {table}");
      }
      _initialized = false;
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task<IEnumerable<string>> GetTablesAsync(CancellationToken ct = default(CancellationToken))
  {
    return await _db.QueryAsync<string>(
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'"
    );
  }

  public async Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default(CancellationToken))
  {
    var pragma = await _db.QueryAsync<dynamic>(
        "PRAGMA table_info(@tableName)",
        new
        {
            tableName
        }
    );

    return pragma.Select(p => new ColumnInfo(
        p.name,
        p.type,
        p.notnull == 0,
        p.pk == 1,
        p.dflt_value
    ));
  }

  public async Task<IEnumerable<IndexInfo>> GetIndexesAsync(string tableName, CancellationToken ct = default(CancellationToken))
  {
    return new List<IndexInfo>();
    //TODO: fix this if needed
    // IEnumerable<IndexInfo> indexes = await _db.QueryAsync<IndexInfo>(
    //     "SELECT * FROM sqlite_master WHERE type = 'index' AND tbl_name = @tableName",
    //     new { tableName }
    // );
    //
    // return indexes.Select(idx => new IndexInfo(
    //     idx.name,
    //     idx.sql.ToString()
    //         .Split('(')[1]
    //         .TrimEnd(')')
    //         .Split(',')
    //         .Select(c => c.Trim())
    //         .ToArray(),
    //     idx.sql.ToString().Contains("UNIQUE"),
    //     "btree" // SQLite only supports btree indexes
    // ));
  }

  private static string GenerateSchema(Type type)
  {
    string? tableName = type.Name.ToLowerInvariant();
    var columns = new List<string>();
    var indices = new List<string>();

    foreach (PropertyInfo? prop in type.GetProperties())
    {
      if (prop.GetCustomAttribute<NotMappedAttribute>() != null) continue;

      string? columnName = prop.Name.ToLowerInvariant();
      string? columnType = GetSQLiteType(prop);
      var constraints = new List<string>();

      if (IsPrimaryKey(prop))
      {
        constraints.Add("PRIMARY KEY");
        if (prop.PropertyType == typeof(int))
        {
          constraints.Add("AUTOINCREMENT");
        }
      }

      if (!IsNullable(prop))
      {
        constraints.Add("NOT NULL");
      }

      if (ShouldIndex(prop))
      {
        indices.Add($"CREATE INDEX IF NOT EXISTS idx_{tableName}_{columnName} ON {tableName}({columnName});");
      }

      columns.Add($"{columnName} {columnType} {string.Join(" ", constraints)}".TrimEnd());
    }

    return $@"
            CREATE TABLE IF NOT EXISTS {tableName} (
                {string.Join(",\n    ", columns)}
            );
            {string.Join("\n", indices)}
        ";
  }

  private static string GetSQLiteType(PropertyInfo prop)
  {
    Type? type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

    return type switch
    {
        Type t when t == typeof(int) => "INTEGER",
        Type t when t == typeof(long) => "INTEGER",
        Type t when t == typeof(string) => "TEXT",
        Type t when t == typeof(DateTime) => "TEXT",
        Type t when t == typeof(bool) => "INTEGER",
        Type t when t == typeof(decimal) => "NUMERIC",
        Type t when t == typeof(double) => "REAL",
        Type t when t == typeof(float) => "REAL",
        Type t when t == typeof(Guid) => "TEXT",
        Type t when t == typeof(byte[]) => "BLOB",
        Type t when t.IsEnum => "INTEGER",
        _ => "TEXT"
    };
  }

  private static bool IsPrimaryKey(PropertyInfo prop)
  {
    return prop.GetCustomAttribute<KeyAttribute>() != null ||
           prop.Name.Equals("Id", StringComparison.OrdinalIgnoreCase);
  }

  private static bool IsNullable(PropertyInfo prop)
  {
    return !prop.GetCustomAttribute<RequiredAttribute>()?.GetType().Name.Contains("Required") ?? true &&
        (Nullable.GetUnderlyingType(prop.PropertyType) != null || prop.PropertyType == typeof(string));
  }

  private static bool ShouldIndex(PropertyInfo prop)
  {
    return prop.GetCustomAttribute<IndexAttribute>() != null ||
           IsPrimaryKey(prop) ||
           prop.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase);
  }
}
