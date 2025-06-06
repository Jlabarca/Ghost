namespace Ghost.Data;

/// <summary>
/// Interface for database schema management
/// </summary>
public interface ISchemaManager
{
    // Single type initialization
    Task InitializeAsync<T>(CancellationToken ct = default) where T : class;
    
    // Multiple types initialization
    Task InitializeAsync(Type[] types, CancellationToken ct = default);
    
    // Schema existence check
    Task<bool> ExistsAsync<T>(CancellationToken ct = default) where T : class;
    
    // Schema validation
    Task<bool> ValidateAsync<T>(CancellationToken ct = default) where T : class;
    
    // Schema migration
    Task MigrateAsync<T>(CancellationToken ct = default) where T : class;
    
    // Schema reset
    Task ResetAsync(CancellationToken ct = default);
    
    // Schema info
    Task<IEnumerable<string>> GetTablesAsync(CancellationToken ct = default);
    Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string tableName, CancellationToken ct = default);
    Task<IEnumerable<IndexInfo>> GetIndexesAsync(string tableName, CancellationToken ct = default);
}
