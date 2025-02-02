using Dapper;
using Ghost.Infrastructure.Storage.Database;
using Ghost.SDK;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Ghost.Infrastructure.Storage;

public interface IDatabaseFactory
{
    Task<IDatabaseClient> CreateClientAsync(DatabaseType type, string connectionString);
    Task<IDatabaseClient> CreateClientAsync(GhostOptions options);
}

public enum DatabaseType
{
    SQLite,
    PostgreSQL
}

public class DatabaseFactory : IDatabaseFactory
{
    private readonly IServiceProvider _services;
    private readonly IDictionary<string, IDatabaseClient> _clients;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DatabaseFactory(IServiceProvider services)
    {
        _services = services;
        _clients = new Dictionary<string, IDatabaseClient>();
    }

    public async Task<IDatabaseClient> CreateClientAsync(DatabaseType type, string connectionString)
    {
        await _lock.WaitAsync();
        try
        {
            var key = $"{type}:{connectionString}";
            if (_clients.TryGetValue(key, out var existingClient))
            {
                return existingClient;
            }

            IDatabaseClient client = type switch
            {
                    DatabaseType.SQLite => new SQLiteClient(connectionString),
                    DatabaseType.PostgreSQL => new PostgresClient(connectionString),
                    _ => throw new ArgumentException($"Unsupported database type: {type}")
            };

            await InitializeDatabaseAsync(client);
            _clients[key] = client;
            return client;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IDatabaseClient> CreateClientAsync(GhostOptions options)
    {
        // Determine database type from options
        if (!options.UseRedis && !string.IsNullOrEmpty(options.PostgresConnectionString))
        {
            return await CreateClientAsync(
                DatabaseType.PostgreSQL, 
                options.PostgresConnectionString);
        }

        // Default to SQLite with data directory
        var dataDir = options.DataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ghost");
        
        Directory.CreateDirectory(dataDir);
        var sqliteConnection = $"Data Source={Path.Combine(dataDir, "ghost.db")}";
        return await CreateClientAsync(DatabaseType.SQLite, sqliteConnection);
    }

    private async Task InitializeDatabaseAsync(IDatabaseClient client)
    {
        // Create required tables
        await client.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS processes (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                status TEXT NOT NULL,
                metadata TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS config (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                scope TEXT NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                modified_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS metrics (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                process_id TEXT NOT NULL,
                metrics TEXT NOT NULL,
                timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (process_id) REFERENCES processes(id)
            );

            CREATE TABLE IF NOT EXISTS permissions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                key TEXT NOT NULL,
                user_id TEXT NOT NULL,
                permission INTEGER NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                UNIQUE(key, user_id)
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                action TEXT NOT NULL,
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                user_id TEXT,
                details TEXT,
                timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            
            CREATE INDEX IF NOT EXISTS idx_metrics_process_time 
                ON metrics(process_id, timestamp);
                
            CREATE INDEX IF NOT EXISTS idx_config_scope 
                ON config(scope);
                
            CREATE INDEX IF NOT EXISTS idx_audit_entity 
                ON audit_log(entity_type, entity_id);
        ");
    }
}

public class DatabaseMigrationManager
{
    private readonly IDatabaseClient _db;
    private readonly ILogger<DatabaseMigrationManager> _logger;

    public DatabaseMigrationManager(
        IDatabaseClient db,
        ILogger<DatabaseMigrationManager> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task MigrateAsync()
    {
        await _db.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS migrations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                version TEXT NOT NULL,
                applied_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
        ");

        (string, Func<Task>)[] migrations = new[]
        {
            ("v1.0.0", CreateInitialSchema),
            ("v1.1.0", AddAuditLog),
            ("v1.2.0", new Func<Task>(AddMetricsPartitioning))
        };

        foreach (var (version, migration) in migrations)
        {
            if (!await IsMigrationAppliedAsync(version))
            {
                _logger.LogInformation("Applying migration {Version}", version);
                await using var transaction = await _db.BeginTransactionAsync();
                try
                {
                    await migration.Invoke();
                    await RecordMigrationAsync(version);
                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    _logger.LogError(ex, "Failed to apply migration {Version}", version);
                    throw;
                }
            }
        }
    }

    private async Task<bool> IsMigrationAppliedAsync(string version)
    {
        var count = await _db.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM migrations WHERE version = @version",
            new { version });
        return count > 0;
    }

    private async Task RecordMigrationAsync(string version)
    {
        await _db.ExecuteAsync(
            "INSERT INTO migrations (version) VALUES (@version)",
            new { version });
    }

    private async Task CreateInitialSchema()
    {
        // Initial schema creation
        await _db.ExecuteAsync(@"
            -- Initial schema SQL here
        ");
    }

    private async Task AddAuditLog()
    {
        await _db.ExecuteAsync(@"
            CREATE TABLE audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                action TEXT NOT NULL,
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                user_id TEXT,
                details TEXT,
                timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            
            CREATE INDEX idx_audit_entity ON audit_log(entity_type, entity_id);
        ");
    }

    private async Task AddMetricsPartitioning()
    {
        // Add partitioning for metrics table (PostgreSQL specific)
        if (_db is PostgresClient)
        {
            await _db.ExecuteAsync(@"
                -- Create partitioned metrics table
                CREATE TABLE IF NOT EXISTS metrics_partitioned (
                    id SERIAL,
                    process_id TEXT NOT NULL,
                    metrics TEXT NOT NULL,
                    timestamp TIMESTAMP NOT NULL
                ) PARTITION BY RANGE (timestamp);

                -- Create partitions by month
                CREATE TABLE metrics_y2024m01 PARTITION OF metrics_partitioned
                    FOR VALUES FROM ('2024-01-01') TO ('2024-02-01');
                
                CREATE TABLE metrics_y2024m02 PARTITION OF metrics_partitioned
                    FOR VALUES FROM ('2024-02-01') TO ('2024-03-01');
                
                -- Add indexes on partitioned table
                CREATE INDEX ON metrics_partitioned (process_id, timestamp);
            ");
        }
    }
}

/// <summary>
/// Advanced query builder for constructing complex database queries
/// </summary>
public class QueryBuilder
{
    private readonly StringBuilder _sql;
    private readonly List<string> _where;
    private readonly List<string> _orderBy;
    private readonly Dictionary<string, object> _parameters;
    private int _paramCount;
    private string _limit;
    private string _offset;

    public QueryBuilder(string baseQuery)
    {
        _sql = new StringBuilder(baseQuery);
        _where = new List<string>();
        _orderBy = new List<string>();
        _parameters = new Dictionary<string, object>();
        _paramCount = 0;
    }

    public QueryBuilder Where(string condition, object value)
    {
        var param = $"@p{++_paramCount}";
        _where.Add(condition.Replace("@value", param));
        _parameters[param] = value;
        return this;
    }

    public QueryBuilder OrderBy(string column, bool ascending = true)
    {
        _orderBy.Add($"{column} {(ascending ? "ASC" : "DESC")}");
        return this;
    }

    public QueryBuilder Limit(int limit)
    {
        _limit = $"LIMIT {limit}";
        return this;
    }

    public QueryBuilder Offset(int offset)
    {
        _offset = $"OFFSET {offset}";
        return this;
    }

    public (string sql, object parameters) Build()
    {
        var query = new StringBuilder(_sql.ToString());

        if (_where.Any())
        {
            query.Append(" WHERE ").Append(string.Join(" AND ", _where));
        }

        if (_orderBy.Any())
        {
            query.Append(" ORDER BY ").Append(string.Join(", ", _orderBy));
        }

        if (_limit != null)
        {
            query.Append(" ").Append(_limit);
        }

        if (_offset != null)
        {
            query.Append(" ").Append(_offset);
        }

        return (query.ToString(), new DynamicParameters(_parameters));
    }
}