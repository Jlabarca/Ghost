using System.Data;
using Dapper;
using Ghost.Core.Storage.Database;
using System.Reflection;

namespace Ghost.Core.Storage;

/// <summary>
/// Extension methods for bulk operations
/// </summary>
public static class BulkOperationExtensions
{
    public static async Task<int> BulkInsertAsync<T>(this IGhostData db, IEnumerable<T> items, string tableName = null)
    {
        if (items == null || !items.Any()) return 0;

        tableName ??= typeof(T).Name.ToLowerInvariant();
        var properties = typeof(T).GetProperties()
            .Where(p => p.GetCustomAttribute<NotMappedAttribute>() == null)
            .ToList();

        var columns = string.Join(", ", properties.Select(p => p.Name.ToLowerInvariant()));
        var parameters = string.Join(", ", properties.Select(p => "@" + p.Name));
        
        var sql = $"INSERT INTO {tableName} ({columns}) VALUES ({parameters})";

        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var count = 0;
            // Process in batches of 1000
            foreach (var batch in items.Chunk(1000))
            {
                count += await db.ExecuteAsync(sql, batch);
            }
            await tx.CommitAsync();
            return count;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public static async Task<int> BulkUpdateAsync<T>(this IGhostData db, 
        IEnumerable<T> items, 
        string keyColumn,
        string tableName = null)
    {
        if (items == null || !items.Any()) return 0;

        tableName ??= typeof(T).Name.ToLowerInvariant();
        var properties = typeof(T).GetProperties()
            .Where(p => p.GetCustomAttribute<NotMappedAttribute>() == null 
                       && p.Name.ToLowerInvariant() != keyColumn.ToLowerInvariant())
            .ToList();

        var updates = string.Join(", ", 
            properties.Select(p => $"{p.Name.ToLowerInvariant()} = @{p.Name}"));
        
        var sql = $"UPDATE {tableName} SET {updates} WHERE {keyColumn} = @{keyColumn}";

        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var count = 0;
            foreach (var batch in items.Chunk(1000))
            {
                count += await db.ExecuteAsync(sql, batch);
            }
            await tx.CommitAsync();
            return count;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public static async Task<int> BulkDeleteAsync<T>(this IGhostData db, 
        IEnumerable<T> keys, 
        string keyColumn = "Id",
        string tableName = null)
    {
        if (keys == null || !keys.Any()) return 0;

        tableName ??= typeof(T).Name.ToLowerInvariant();
        
        // For PostgreSQL we can use = ANY
        var sql = db.DatabaseType == DatabaseType.PostgreSQL
            ? $"DELETE FROM {tableName} WHERE {keyColumn} = ANY(@keys)"
            : $"DELETE FROM {tableName} WHERE {keyColumn} IN @keys";

        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var count = await db.ExecuteAsync(sql, new { keys });
            await tx.CommitAsync();
            return count;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public static async Task<int> BulkMergeAsync<T>(this IGhostData db,
        IEnumerable<T> items,
        string keyColumn,
        string tableName = null)
    {
        if (items == null || !items.Any()) return 0;

        tableName ??= typeof(T).Name.ToLowerInvariant();
        var properties = typeof(T).GetProperties()
            .Where(p => p.GetCustomAttribute<NotMappedAttribute>() == null)
            .ToList();

        var columns = string.Join(", ", properties.Select(p => p.Name.ToLowerInvariant()));
        var values = string.Join(", ", properties.Select(p => "@" + p.Name));
        var updates = string.Join(", ", 
            properties.Where(p => p.Name.ToLowerInvariant() != keyColumn.ToLowerInvariant())
                .Select(p => $"{p.Name.ToLowerInvariant()} = EXCLUDED.{p.Name.ToLowerInvariant()}"));

        var sql = db.DatabaseType switch
        {
            // PostgreSQL UPSERT
            DatabaseType.PostgreSQL => $@"
                INSERT INTO {tableName} ({columns})
                VALUES ({values})
                ON CONFLICT ({keyColumn})
                DO UPDATE SET {updates}",

            // SQLite UPSERT
            DatabaseType.SQLite => $@"
                INSERT INTO {tableName} ({columns})
                VALUES ({values})
                ON CONFLICT({keyColumn})
                DO UPDATE SET {updates}",

            _ => throw new NotSupportedException($"Database type {db.DatabaseType} not supported")
        };

        await using var tx = await db.BeginTransactionAsync();
        try
        {
            var count = 0;
            foreach (var batch in items.Chunk(1000))
            {
                count += await db.ExecuteAsync(sql, batch);
            }
            await tx.CommitAsync();
            return count;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Copy table data in bulk using database-specific methods
    /// </summary>
    public static async Task<int> BulkCopyAsync<T>(this IGhostData db,
        DataTable data,
        string tableName = null)
    {
        if (data == null || data.Rows.Count == 0) return 0;

        tableName ??= typeof(T).Name.ToLowerInvariant();

        switch (db.DatabaseType)
        {
            case DatabaseType.PostgreSQL:
                if (db is not IGhostData ghostData)
                    throw new InvalidOperationException("Invalid database client type");

                var dbClient = ghostData.GetDatabaseClient();
                if (dbClient is not PostgresClient postgresClient)
                    throw new InvalidOperationException("Invalid database client type");

                return await postgresClient.BulkCopyAsync(data, tableName);

            case DatabaseType.SQLite:
                // SQLite doesn't have native bulk copy, use batched inserts
                var columns = string.Join(", ", data.Columns.Cast<DataColumn>().Select(c => c.ColumnName));
                var parameters = string.Join(", ", data.Columns.Cast<DataColumn>().Select(c => "@" + c.ColumnName));
                var sql = $"INSERT INTO {tableName} ({columns}) VALUES ({parameters})";

                var tx = await db.BeginTransactionAsync();
                try
                {
                    var count = 0;
                    foreach (DataRow row in data.Rows)
                    {
                        var param = new DynamicParameters();
                        foreach (DataColumn col in data.Columns)
                        {
                            param.Add("@" + col.ColumnName, row[col]);
                        }
                        count += await db.ExecuteAsync(sql, param);
                    }
                    await tx.CommitAsync();
                    return count;
                }
                catch
                {
                    await tx.RollbackAsync();
                    throw;
                }

            default:
                throw new NotSupportedException($"Database type {db.DatabaseType} not supported");
        }
    }
}