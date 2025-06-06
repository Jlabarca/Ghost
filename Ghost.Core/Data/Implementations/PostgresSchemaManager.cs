using Ghost.Exceptions;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace Ghost.Data.Implementations
{
  /// <summary>
  /// PostgreSQL implementation of schema management functionality.
  /// Provides PostgreSQL-specific schema operations like table creation,
  /// validation, and migration.
  /// </summary>
  public class PostgresSchemaManager : ISchemaManager
  {
    private readonly IDatabaseClient _db;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresSchemaManager"/> class.
    /// </summary>
    /// <param name="db">The database client.</param>
    /// <param name="logger">The logger.</param>
    public PostgresSchemaManager(IDatabaseClient db)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Initializes the schema for a specific type.
    /// </summary>
    /// <typeparam name="T">The entity type to initialize schema for.</typeparam>
    /// <param name="ct">Optional cancellation token.</param>
    public async Task InitializeAsync<T>(CancellationToken ct = default) where T : class
    {
      await _lock.WaitAsync(ct);
      try
      {
        G.LogDebug("Initializing schema for {Type}", typeof(T).Name);
        await InitializeBaseTablesAsync(ct);

        // Get entity table information
        var tableName = GetTableName<T>();
        var columns = GetColumns<T>();

        // Check if table exists
        var tableExists = await _db.TableExistsAsync(tableName, ct);
        if (!tableExists)
        {
          await CreateTableAsync(tableName, columns, ct);
          G.LogInfo("Created table {TableName}", tableName);
        } else
        {
          // Validate that the table has the correct schema
          var isValid = await ValidateTableAsync(tableName, columns, ct);
          if (!isValid)
          {
            G.LogWarn("Table {TableName} schema is invalid. Migration may be required.", tableName);
          }
        }

        // Create indexes
        await CreateIndexesAsync<T>(tableName, ct);
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error initializing schema for {Type}", typeof(T).Name);
        throw new GhostException($"Failed to initialize schema for {typeof(T).Name}", ex, ErrorCode.StorageConfigurationFailed);
      }
      finally
      {
        _lock.Release();
      }
    }

    /// <summary>
    /// Initializes the schema for multiple types.
    /// </summary>
    /// <param name="types">The entity types to initialize schema for.</param>
    /// <param name="ct">Optional cancellation token.</param>
    public async Task InitializeAsync(Type[] types, CancellationToken ct = default)
    {
      if (types == null || types.Length == 0)
      {
        throw new ArgumentException("Types cannot be null or empty", nameof(types));
      }

      await _lock.WaitAsync(ct);
      try
      {
        // Initialize base tables first
        await InitializeBaseTablesAsync(ct);

        // Then initialize each entity type
        foreach (var type in types)
        {
          G.LogDebug("Initializing schema for {Type}", type.Name);

          // Use reflection to call the generic method
          var method = typeof(PostgresSchemaManager)
              .GetMethod(nameof(InitializeAsync), new[]
              {
                  typeof(CancellationToken)
              })
              .MakeGenericMethod(type);

          await (Task)method.Invoke(this, new object[]
          {
              ct
          });
        }

        _initialized = true;
        G.LogInfo($"Schema initialization completed for {types.Length} types");
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error initializing schema for multiple types");
        throw new GhostException("Failed to initialize schema for multiple types", ex, ErrorCode.StorageConfigurationFailed);
      }
      finally
      {
        _lock.Release();
      }
    }

    /// <summary>
    /// Checks if the schema exists for a specific type.
    /// </summary>
    /// <typeparam name="T">The entity type to check.</typeparam>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>True if the schema exists; otherwise, false.</returns>
    public async Task<bool> ExistsAsync<T>(CancellationToken ct = default) where T : class
    {
      var tableName = GetTableName<T>();
      return await _db.TableExistsAsync(tableName, ct);
    }

    /// <summary>
    /// Validates that the schema for a specific type is correct.
    /// </summary>
    /// <typeparam name="T">The entity type to validate.</typeparam>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>True if the schema is valid; otherwise, false.</returns>
    public async Task<bool> ValidateAsync<T>(CancellationToken ct = default) where T : class
    {
      var tableName = GetTableName<T>();
      var columns = GetColumns<T>();

      return await ValidateTableAsync(tableName, columns, ct);
    }

    /// <summary>
    /// Migrates the schema for a specific type to the latest version.
    /// </summary>
    /// <typeparam name="T">The entity type to migrate.</typeparam>
    /// <param name="ct">Optional cancellation token.</param>
    public async Task MigrateAsync<T>(CancellationToken ct = default) where T : class
    {
      await _lock.WaitAsync(ct);
      try
      {
        G.LogInfo("Migrating schema for {Type}", typeof(T).Name);

        var tableName = GetTableName<T>();
        var expectedColumns = GetColumns<T>();

        // Check if table exists
        var tableExists = await _db.TableExistsAsync(tableName, ct);
        if (!tableExists)
        {
          await CreateTableAsync(tableName, expectedColumns, ct);
          return;
        }

        // Get current table columns
        var currentColumns = await GetColumnsAsync(tableName, ct);

        // Find columns to add
        var columnsToAdd = expectedColumns
            .Where(ec => !currentColumns.Any(cc => string.Equals(cc.Name, ec.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Add missing columns
        foreach (var column in columnsToAdd)
        {
          await AddColumnAsync(tableName, column, ct);
          G.LogInfo($"Added column {column.Name} to table {tableName}");
        }

        // We don't remove or modify columns for safety reasons

        // Update indexes
        await CreateIndexesAsync<T>(tableName, ct);

        G.LogInfo("Migration completed for {Type}", typeof(T).Name);
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error migrating schema for {Type}", typeof(T).Name);
        throw new GhostException($"Failed to migrate schema for {typeof(T).Name}", ex, ErrorCode.StorageConfigurationFailed);
      }
      finally
      {
        _lock.Release();
      }
    }

    /// <summary>
    /// Resets the entire schema, removing all tables.
    /// Warning: This will delete all data.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    public async Task ResetAsync(CancellationToken ct = default)
    {
      await _lock.WaitAsync(ct);
      try
      {
        G.LogWarn("Resetting entire database schema");

        // Get all table names
        var tables = await GetTablesAsync(ct);

        // Drop all tables
        foreach (var table in tables)
        {
          await DropTableAsync(table, ct);
          G.LogInfo("Dropped table {TableName}", table);
        }

        _initialized = false;
        G.LogWarn("Database schema reset completed");
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error resetting schema");
        throw new GhostException("Failed to reset schema", ex, ErrorCode.StorageConfigurationFailed);
      }
      finally
      {
        _lock.Release();
      }
    }

    /// <summary>
    /// Gets all table names in the database.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The table names.</returns>
    public async Task<IEnumerable<string>> GetTablesAsync(CancellationToken ct = default)
    {
      try
      {
        return await _db.GetTableNamesAsync(ct);
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error getting table names");
        throw new GhostException("Failed to get table names", ex, ErrorCode.StorageOperationFailed);
      }
    }

    /// <summary>
    /// Gets the columns for a specific table.
    /// </summary>
    /// <param name="tableName">The name of the table.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The columns in the table.</returns>
    public async Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default)
    {
      try
      {
        const string sql = @"
                    SELECT
                        column_name as Name,
                        data_type as Type,
                        is_nullable = 'YES' as IsNullable,
                        column_default as DefaultValue,
                        (SELECT EXISTS (
                            SELECT 1
                            FROM information_schema.table_constraints tc
                            JOIN information_schema.constraint_column_usage ccu ON tc.constraint_name = ccu.constraint_name
                            WHERE tc.constraint_type = 'PRIMARY KEY'
                            AND tc.table_name = c.table_name
                            AND ccu.column_name = c.column_name
                        )) as IsPrimaryKey,
                        (column_default LIKE '%nextval%') as IsAutoIncrement
                    FROM information_schema.columns c
                    WHERE table_name = @tableName
                    ORDER BY ordinal_position";

        var columns = await _db.QueryAsync<PostgresColumnInfo>(sql, new
        {
            tableName
        }, ct);

        return columns.Select(c => new ColumnInfo(
            name: c.Name,
            type: c.Type,
            isNullable: c.IsNullable,
            isPrimaryKey: c.IsPrimaryKey,
            isAutoIncrement: c.IsAutoIncrement,
            defaultValue: c.DefaultValue
        ));
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error getting columns for table {TableName}", tableName);
        throw new GhostException($"Failed to get columns for table {tableName}", ex, ErrorCode.StorageOperationFailed);
      }
    }

    /// <summary>
    /// Gets the indexes for a specific table.
    /// </summary>
    /// <param name="tableName">The name of the table.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The indexes on the table.</returns>
    public async Task<IEnumerable<IndexInfo>> GetIndexesAsync(string tableName, CancellationToken ct = default)
    {
      try
      {
        const string sql = @"
                    SELECT
                        i.relname as Name,
                        ix.indisunique as IsUnique,
                        pg_get_expr(ix.indpred, ix.indrelid) as Filter,
                        array_agg(a.attname) as Columns
                    FROM
                        pg_index ix
                    JOIN
                        pg_class i ON i.oid = ix.indexrelid
                    JOIN
                        pg_class t ON t.oid = ix.indrelid
                    JOIN
                        pg_namespace n ON n.oid = t.relnamespace
                    JOIN
                        pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
                    WHERE
                        t.relname = @tableName
                    GROUP BY
                        i.relname, ix.indisunique, ix.indpred, ix.indrelid
                    ORDER BY
                        i.relname";

        var indexes = await _db.QueryAsync<PostgresIndexInfo>(sql, new
        {
            tableName
        }, ct);

        return indexes.Select(i => new IndexInfo
        {
            Name = i.Name,
            IsUnique = i.IsUnique,
            Filter = i.Filter,
            Columns = i.Columns ?? Array.Empty<string>()
        });
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error getting indexes for table {TableName}", tableName);
        throw new GhostException($"Failed to get indexes for table {tableName}", ex, ErrorCode.StorageOperationFailed);
      }
    }

        #region Helper Methods

    /// <summary>
    /// Initializes the base system tables required for the application.
    /// </summary>
    private async Task InitializeBaseTablesAsync(CancellationToken ct)
    {
      // Create system tables like migrations, settings, etc.

      // Example: Migrations table
      const string createMigrationsTable = @"
                CREATE TABLE IF NOT EXISTS _migrations (
                    id SERIAL PRIMARY KEY,
                    name VARCHAR(255) NOT NULL,
                    applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    content TEXT
                )";

      // Example: Settings table
      const string createSettingsTable = @"
                CREATE TABLE IF NOT EXISTS _settings (
                    key VARCHAR(255) PRIMARY KEY,
                    value TEXT,
                    type VARCHAR(50),
                    updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
                )";

      // Execute the creation scripts
      await _db.ExecuteAsync(createMigrationsTable, null, ct);
      await _db.ExecuteAsync(createSettingsTable, null, ct);
    }

    /// <summary>
    /// Creates a new table with the specified columns.
    /// </summary>
    private async Task CreateTableAsync(string tableName, IEnumerable<ColumnInfo> columns, CancellationToken ct)
    {
      var columnsArray = columns.ToArray();

      // Build the CREATE TABLE statement
      var createTableSql = new StringBuilder();
      createTableSql.AppendLine($"CREATE TABLE {tableName} (");

      for(var i = 0; i < columnsArray.Length; i++)
      {
        var column = columnsArray[i];
        var columnDef = new StringBuilder();

        // Add column name and type
        columnDef.Append($"  {column.Name} {MapToPgType(column.Type)}");

        // Add nullable constraint
        if (!column.IsNullable)
        {
          columnDef.Append(" NOT NULL");
        }

        // Add primary key constraint
        if (column.IsPrimaryKey)
        {
          columnDef.Append(" PRIMARY KEY");
        }

        // Add auto-increment constraint
        if (column.IsAutoIncrement)
        {
          // For PostgreSQL, we use SERIAL or BIGSERIAL types for auto-increment
          // This is already handled in the type mapping
        }

        // Add default value
        if (!string.IsNullOrEmpty(column.DefaultValue))
        {
          columnDef.Append($" DEFAULT {column.DefaultValue}");
        }

        // Add comma if not the last column
        if (i < columnsArray.Length - 1)
        {
          columnDef.Append(",");
        }

        createTableSql.AppendLine(columnDef.ToString());
      }

      createTableSql.AppendLine(")");

      // Execute the CREATE TABLE statement
      await _db.ExecuteAsync(createTableSql.ToString(), null, ct);
    }

    /// <summary>
    /// Adds a new column to an existing table.
    /// </summary>
    private async Task AddColumnAsync(string tableName, ColumnInfo column, CancellationToken ct)
    {
      var addColumnSql = $"ALTER TABLE {tableName} ADD COLUMN {column.Name} {MapToPgType(column.Type)}";

      // Add nullable constraint
      if (!column.IsNullable)
      {
        addColumnSql += " NOT NULL";
      }

      // Add default value
      if (!string.IsNullOrEmpty(column.DefaultValue))
      {
        addColumnSql += $" DEFAULT {column.DefaultValue}";
      }

      // Execute the ALTER TABLE statement
      await _db.ExecuteAsync(addColumnSql, null, ct);
    }

    /// <summary>
    /// Creates the indexes for an entity type.
    /// </summary>
    private async Task CreateIndexesAsync<T>(string tableName, CancellationToken ct) where T : class
    {
      // This is a placeholder. In a real implementation, you would:
      // 1. Analyze the entity type for [Index] attributes
      // 2. Check existing indexes
      // 3. Create missing indexes

      // Example of creating a basic index
      var indexName = $"idx_{tableName}_id";
      var createIndexSql = $"CREATE INDEX IF NOT EXISTS {indexName} ON {tableName} (id)";

      await _db.ExecuteAsync(createIndexSql, null, ct);
    }

    /// <summary>
    /// Drops a table from the database.
    /// </summary>
    private async Task DropTableAsync(string tableName, CancellationToken ct)
    {
      var dropTableSql = $"DROP TABLE IF EXISTS {tableName} CASCADE";
      await _db.ExecuteAsync(dropTableSql, null, ct);
    }

    /// <summary>
    /// Validates that a table has the expected column structure.
    /// </summary>
    private async Task<bool> ValidateTableAsync(string tableName, IEnumerable<ColumnInfo> expectedColumns, CancellationToken ct)
    {
      var actualColumns = await GetColumnsAsync(tableName, ct);
      var actualColumnDict = actualColumns.ToDictionary(c => c.Name.ToLowerInvariant());

      foreach (var expectedColumn in expectedColumns)
      {
        var columnName = expectedColumn.Name.ToLowerInvariant();

        // Check if column exists
        if (!actualColumnDict.TryGetValue(columnName, out var actualColumn))
        {
          G.LogWarn($"Column {expectedColumn.Name} does not exist in table {tableName}");
          return false;
        }

        // Check column type
        if (!string.Equals(NormalizeType(actualColumn.Type), NormalizeType(expectedColumn.Type), StringComparison.OrdinalIgnoreCase))
        {
          G.LogWarn($"Column {expectedColumn.Name} has incorrect type. Expected: {expectedColumn.Type}, Actual: {actualColumn.Type}");
          return false;
        }

        // Check nullability
        if (actualColumn.IsNullable != expectedColumn.IsNullable)
        {
          G.LogWarn($"Column {expectedColumn.Name} has incorrect nullability. Expected: {expectedColumn.IsNullable}, Actual: {actualColumn.IsNullable}");
          return false;
        }

        // Check primary key
        if (actualColumn.IsPrimaryKey != expectedColumn.IsPrimaryKey)
        {
          G.LogWarn($"Column {expectedColumn.Name} has incorrect primary key status. Expected: {expectedColumn.IsPrimaryKey}, Actual: {actualColumn.IsPrimaryKey}");
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// Gets the table name for an entity type.
    /// </summary>
    private string GetTableName<T>() where T : class
    {
      // In a real implementation, this would check for [Table] attributes
      // and follow naming conventions.
      // Here, we just use the plural form of the type name
      return typeof(T).Name.ToLowerInvariant() + "s";
    }

    /// <summary>
    /// Gets the columns for an entity type.
    /// </summary>
    private IEnumerable<ColumnInfo> GetColumns<T>() where T : class
    {
      // In a real implementation, this would use reflection to analyze
      // the entity type and attributes like [Column], [Required], etc.
      // Here, we just provide a simple example

      var columns = new List<ColumnInfo>();

      // Add standard columns
      columns.Add(new ColumnInfo(
          name: "id",
          type: "int",
          isNullable: false,
          isPrimaryKey: true,
          isAutoIncrement: true
      ));

      // Add properties as columns
      foreach (var prop in typeof(T).GetProperties())
      {
        // Skip the Id property, we've already added it
        if (string.Equals(prop.Name, "Id", StringComparison.OrdinalIgnoreCase))
          continue;

        // Map property to column
        columns.Add(new ColumnInfo(
            name: prop.Name.ToLowerInvariant(),
            type: MapCSharpTypeToDbType(prop.PropertyType),
            isNullable: IsNullableType(prop.PropertyType),
            isPrimaryKey: false,
            isAutoIncrement: false
        ));
      }

      return columns;
    }

    /// <summary>
    /// Maps a C# type to a database type.
    /// </summary>
    private string MapCSharpTypeToDbType(Type type)
    {
      // Handle nullable types
      if (IsNullableType(type))
      {
        type = Nullable.GetUnderlyingType(type);
      }

      if (type == typeof(int))
        return "int";
      if (type == typeof(long))
        return "bigint";
      if (type == typeof(string))
        return "text";
      if (type == typeof(bool))
        return "boolean";
      if (type == typeof(DateTime))
        return "timestamp";
      if (type == typeof(decimal))
        return "decimal";
      if (type == typeof(double))
        return "double precision";
      if (type == typeof(float))
        return "real";
      if (type == typeof(Guid))
        return "uuid";
      if (type == typeof(byte[]))
        return "bytea";

      // Default to text for complex types
      return "text";
    }

    /// <summary>
    /// Maps a column type to a PostgreSQL type.
    /// </summary>
    private string MapToPgType(string type)
    {
      switch (type.ToLowerInvariant())
      {
        case "int":
          return "INTEGER";
        case "bigint":
          return "BIGINT";
        case "text":
          return "TEXT";
        case "string":
          return "TEXT";
        case "boolean":
          return "BOOLEAN";
        case "bool":
          return "BOOLEAN";
        case "datetime":
          return "TIMESTAMP";
        case "timestamp":
          return "TIMESTAMP";
        case "decimal":
          return "DECIMAL";
        case "double":
        case "double precision":
          return "DOUBLE PRECISION";
        case "float":
        case "real":
          return "REAL";
        case "guid":
        case "uuid":
          return "UUID";
        case "byte[]":
        case "bytea":
          return "BYTEA";
        default:
          return "TEXT";
      }
    }

    /// <summary>
    /// Normalizes a type name for comparison.
    /// </summary>
    private string NormalizeType(string type)
    {
      if (string.IsNullOrEmpty(type))
        return string.Empty;

      // Remove size specifications like (255)
      var normalized = Regex.Replace(type, @"\(\d+\)", string.Empty);

      // Convert to lowercase
      normalized = normalized.ToLowerInvariant();

      // Normalize common type names
      switch (normalized)
      {
        case "int4":
        case "integer":
          return "int";
        case "int8":
        case "bigint":
          return "bigint";
        case "varchar":
        case "character varying":
        case "text":
          return "text";
        case "bool":
        case "boolean":
          return "boolean";
        case "timestamp":
        case "timestamp without time zone":
        case "date":
          return "timestamp";
        case "numeric":
        case "decimal":
          return "decimal";
        case "float8":
        case "double precision":
          return "double";
        case "float4":
        case "real":
          return "float";
        default:
          return normalized;
      }
    }

    /// <summary>
    /// Checks if a type is nullable.
    /// </summary>
    private bool IsNullableType(Type type)
    {
      // Check if it's a nullable value type
      if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        return true;

      // Reference types are nullable by default
      return !type.IsValueType;
    }

        #endregion

        #region PostgreSQL-specific classes

    /// <summary>
    /// PostgreSQL-specific column information used for querying.
    /// </summary>
    private class PostgresColumnInfo
    {
      public string Name { get; set; }
      public string Type { get; set; }
      public bool IsNullable { get; set; }
      public bool IsPrimaryKey { get; set; }
      public bool IsAutoIncrement { get; set; }
      public string DefaultValue { get; set; }
    }

    /// <summary>
    /// PostgreSQL-specific index information used for querying.
    /// </summary>
    private class PostgresIndexInfo
    {
      public string Name { get; set; }
      public bool IsUnique { get; set; }
      public string Filter { get; set; }
      public string[] Columns { get; set; }
    }

        #endregion
  }
}
