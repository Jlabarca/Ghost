# PowerShell script to restructure Ghost project for enhanced architecture
# Improved version with aggressive data layer cleanup

# Base paths
$basePath = Get-Location
$corePath = Join-Path $basePath "Ghost.Core"
$sdkPath = Join-Path $basePath "Ghost.SDK"
$fatherPath = Join-Path $basePath "Ghost.Father"
$postgresPath = Join-Path $basePath "Ghost.Postgres"

# Backup directory
$backupDir = Join-Path $basePath "GhostMigration_Backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"

# Counter for tracking changes
$stats = @{
    "DirectoriesCreated" = 0
    "DirectoriesRemoved" = 0
    "FilesCreated" = 0
    "FilesRemoved" = 0
    "FilesBackedUp" = 0
}

# Create directory if it doesn't exist
function Create-DirectoryIfNotExists {
    param([string]$path)
    if (-not (Test-Path $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        Write-Host "Created directory: $path" -ForegroundColor Green
        $script:stats.DirectoriesCreated++
    }
}

# Remove directory if it exists
function Remove-DirectoryIfExists {
    param(
        [string]$path,
        [switch]$backup
    )
    if (Test-Path $path) {
        # Backup if requested
        if ($backup) {
            $relativePath = $path.Substring($basePath.Length).TrimStart('\', '/')
            $backupPath = Join-Path $backupDir $relativePath
            Create-DirectoryIfNotExists (Split-Path $backupPath -Parent)
            
            # Backup all files in the directory
            Get-ChildItem -Path $path -Recurse -File | ForEach-Object {
                $relativeFilePath = $_.FullName.Substring($path.Length).TrimStart('\', '/')
                $backupFilePath = Join-Path $backupPath $relativeFilePath
                Create-DirectoryIfNotExists (Split-Path $backupFilePath -Parent)
                Copy-Item -Path $_.FullName -Destination $backupFilePath -Force
                $script:stats.FilesBackedUp++
            }
            
            Write-Host "Backed up directory: $path to $backupPath" -ForegroundColor Cyan
        }
        
        # Remove the directory
        Remove-Item -Path $path -Recurse -Force
        Write-Host "Removed directory: $path" -ForegroundColor Yellow
        $script:stats.DirectoriesRemoved++
    }
}

# Create new file
function Create-File {
    param(
        [string]$path,
        [string]$content
    )
    $dirPath = Split-Path $path -Parent
    Create-DirectoryIfNotExists $dirPath
    
    $content | Out-File -FilePath $path -Encoding UTF8
    Write-Host "Created file: $path" -ForegroundColor Green
    $script:stats.FilesCreated++
}

# Remove file if exists
function Remove-FileIfExists {
    param(
        [string]$path,
        [switch]$backup
    )
    if (Test-Path $path) {
        # Backup if requested
        if ($backup) {
            $relativePath = $path.Substring($basePath.Length).TrimStart('\', '/')
            $backupPath = Join-Path $backupDir $relativePath
            Create-DirectoryIfNotExists (Split-Path $backupPath -Parent)
            Copy-Item -Path $path -Destination $backupPath -Force
            $script:stats.FilesBackedUp++
            Write-Host "Backed up file: $path to $backupPath" -ForegroundColor Cyan
        }
        
        # Remove the file
        Remove-Item -Path $path -Force
        Write-Host "Removed file: $path" -ForegroundColor Yellow
        $script:stats.FilesRemoved++
    }
}

# Main script execution
Write-Host "Starting Ghost architecture restructuring..." -ForegroundColor Cyan
Write-Host "This script will perform significant changes to your codebase." -ForegroundColor Yellow
Write-Host "A backup will be created at: $backupDir" -ForegroundColor Yellow

# Ask for confirmation
$confirmation = Read-Host "Do you want to continue? (Y/N)"
if ($confirmation -ne "Y" -and $confirmation -ne "y") {
    Write-Host "Operation cancelled by user." -ForegroundColor Red
    exit
}

# Create backup directory
Create-DirectoryIfNotExists $backupDir

# STEP 1: Backup and remove the entire Ghost.Core/Data folder
$dataPath = Join-Path $corePath "Data"
Write-Host "`nBacking up and removing entire Data folder..." -ForegroundColor Cyan
Remove-DirectoryIfExists -path $dataPath -backup

# STEP 2: Create new core directory structure
Write-Host "`nCreating new folder structure..." -ForegroundColor Cyan

# Data folder and subfolders
Create-DirectoryIfNotExists (Join-Path $corePath "Data")
Create-DirectoryIfNotExists (Join-Path $corePath "Data\Interfaces")
Create-DirectoryIfNotExists (Join-Path $corePath "Data\Implementations")
Create-DirectoryIfNotExists (Join-Path $corePath "Data\Decorators")
Create-DirectoryIfNotExists (Join-Path $corePath "Data\Abstractions")
Create-DirectoryIfNotExists (Join-Path $corePath "Data\Providers")

# Resilience
Create-DirectoryIfNotExists (Join-Path $corePath "Resilience")
Create-DirectoryIfNotExists (Join-Path $corePath "Resilience\Policies")
Create-DirectoryIfNotExists (Join-Path $corePath "Resilience\Handlers")

# Observability
Create-DirectoryIfNotExists (Join-Path $corePath "Observability")
Create-DirectoryIfNotExists (Join-Path $corePath "Observability\Metrics")
Create-DirectoryIfNotExists (Join-Path $corePath "Observability\Tracing")
Create-DirectoryIfNotExists (Join-Path $corePath "Observability\Health")
Create-DirectoryIfNotExists (Join-Path $corePath "Observability\Logging")

# Configuration
Create-DirectoryIfNotExists (Join-Path $corePath "Configuration")
Create-DirectoryIfNotExists (Join-Path $corePath "Configuration\Options")
Create-DirectoryIfNotExists (Join-Path $corePath "Configuration\Validators")

# Caching
Create-DirectoryIfNotExists (Join-Path $corePath "Caching")
Create-DirectoryIfNotExists (Join-Path $corePath "Caching\L1")
Create-DirectoryIfNotExists (Join-Path $corePath "Caching\L2")
Create-DirectoryIfNotExists (Join-Path $corePath "Caching\Distributed")

# Messaging
Create-DirectoryIfNotExists (Join-Path $corePath "Messaging")
Create-DirectoryIfNotExists (Join-Path $corePath "Messaging\Patterns")
Create-DirectoryIfNotExists (Join-Path $corePath "Messaging\Transport")

# Transactions
Create-DirectoryIfNotExists (Join-Path $corePath "Transactions")
Create-DirectoryIfNotExists (Join-Path $corePath "Transactions\Saga")

# Orchestration
Create-DirectoryIfNotExists (Join-Path $corePath "Orchestration")
Create-DirectoryIfNotExists (Join-Path $corePath "Orchestration\Processes")

# Pooling
Create-DirectoryIfNotExists (Join-Path $corePath "Pooling")

# Testing
Create-DirectoryIfNotExists (Join-Path $corePath "Testing")
Create-DirectoryIfNotExists (Join-Path $corePath "Testing\InMemory")
Create-DirectoryIfNotExists (Join-Path $corePath "Testing\Mocks")

# STEP 3: Create new files
Write-Host "`nCreating new files..." -ForegroundColor Cyan

# Data Layer Interfaces
Create-File -path (Join-Path $corePath "Data\Interfaces\IGhostData.cs") -content @'
namespace Ghost.Core.Data.Interfaces;

/// <summary>
/// Core data access interface for Ghost applications
/// </summary>
public interface IGhostData : IAsyncDisposable
{
    // Single operations
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    
    // Batch operations for performance
    Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default);
    Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default);
    
    // SQL operations
    Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default);
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default);
    Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default);
    Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default);
    
    // Transaction support
    Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default);
    
    // Schema operations
    Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default);
    Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default);
    
    // Access to underlying providers
    IDatabaseClient GetDatabaseClient();
    DatabaseType DatabaseType { get; }
}
'@

Create-File -path (Join-Path $corePath "Data\Interfaces\IGhostTransaction.cs") -content @'
namespace Ghost.Core.Data.Interfaces;

/// <summary>
/// Represents a database transaction
/// </summary>
public interface IGhostTransaction : IAsyncDisposable
{
    Task CommitAsync();
    Task RollbackAsync();
}
'@

Create-File -path (Join-Path $corePath "Data\Interfaces\IDatabaseClient.cs") -content @'
namespace Ghost.Core.Data.Interfaces;

/// <summary>
/// Low-level database client interface
/// </summary>
public interface IDatabaseClient : IStorageProvider
{
    // SQL operations
    Task<T?> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default);
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default);
    Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default);
    Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default);

    // Schema operations
    Task<bool> TableExistsAsync(string tableName, CancellationToken ct = default);
    Task<IEnumerable<string>> GetTableNamesAsync(CancellationToken ct = default);
    
    // Database type
    DatabaseType DatabaseType { get; }
}
'@

Create-File -path (Join-Path $corePath "Data\Interfaces\ICacheProvider.cs") -content @'
namespace Ghost.Core.Data.Interfaces;

/// <summary>
/// Interface for cache providers
/// </summary>
public interface ICacheProvider : IStorageProvider
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<bool> ExpireAsync(string key, TimeSpan expiry, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
    
    string Name { get; }
    CacheCapabilities Capabilities { get; }
}
'@

Create-File -path (Join-Path $corePath "Data\Interfaces\IStorageProvider.cs") -content @'
namespace Ghost.Core.Data.Interfaces;

/// <summary>
/// Base interface for all storage providers
/// </summary>
public interface IStorageProvider : IAsyncDisposable
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<long> GetStorageSizeAsync(CancellationToken ct = default);
}
'@

Create-File -path (Join-Path $corePath "Data\Interfaces\ISchemaManager.cs") -content @'
namespace Ghost.Core.Data.Interfaces;

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
'@

# Core Implementations
Create-File -path (Join-Path $corePath "Data\Implementations\CoreGhostData.cs") -content @'
namespace Ghost.Core.Data.Implementations;

using Ghost.Core.Data.Interfaces;

/// <summary>
/// Core implementation of IGhostData with all required functionality
/// </summary>
public class CoreGhostData : IGhostData
{
    private readonly IDatabaseClient _db;
    private readonly ICacheProvider _cache;
    private readonly IKeyValueStore _kvStore;
    private readonly ISchemaManager _schema;
    private readonly TimeSpan _defaultCacheExpiry = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public DatabaseType DatabaseType => _db.DatabaseType;
    public ISchemaManager Schema => _schema;

    public CoreGhostData(
        IDatabaseClient db, 
        ICacheProvider cache,
        IKeyValueStore kvStore, 
        ISchemaManager schema)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _kvStore = kvStore ?? throw new ArgumentNullException(nameof(kvStore));
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
    }

    public IDatabaseClient GetDatabaseClient() => _db;

    // Implementation details would go here...
    
    // Dispose pattern implementation
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            
            if (_db is IAsyncDisposable dbDisposable) 
                await dbDisposable.DisposeAsync();
                
            if (_kvStore is IAsyncDisposable kvDisposable) 
                await kvDisposable.DisposeAsync();
                
            if (_cache is IAsyncDisposable cacheDisposable) 
                await cacheDisposable.DisposeAsync();
                
            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }
}
'@

# Data Layer Decorators
Create-File -path (Join-Path $corePath "Data\Decorators\CachedGhostData.cs") -content @'
namespace Ghost.Core.Data.Decorators;

using Ghost.Core.Data.Interfaces;
using Ghost.Core.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Decorator that adds caching to any IGhostData implementation
/// </summary>
public class CachedGhostData : IGhostData
{
    private readonly IGhostData _inner;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptions<CachingConfiguration> _config;
    
    public CachedGhostData(IGhostData inner, IMemoryCache memoryCache, IOptions<CachingConfiguration> config)
    {
        _inner = inner;
        _memoryCache = memoryCache;
        _config = config;
    }
    
    public DatabaseType DatabaseType => _inner.DatabaseType;
    
    public IDatabaseClient GetDatabaseClient() => _inner.GetDatabaseClient();
    
    // Implementation would delegate to inner with caching logic
    
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
    }
}
'@

Create-File -path (Join-Path $corePath "Data\Decorators\InstrumentedGhostData.cs") -content @'
namespace Ghost.Core.Data.Decorators;

using Ghost.Core.Data.Interfaces;
using Ghost.Core.Observability.Metrics;
using Microsoft.Extensions.Logging;

/// <summary>
/// Decorator that adds metrics and logging to any IGhostData implementation
/// </summary>
public class InstrumentedGhostData : IGhostData
{
    private readonly IGhostData _inner;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<InstrumentedGhostData> _logger;
    
    public InstrumentedGhostData(
        IGhostData inner, 
        IMetricsCollector metrics, 
        ILogger<InstrumentedGhostData> logger)
    {
        _inner = inner;
        _metrics = metrics;
        _logger = logger;
    }
    
    public DatabaseType DatabaseType => _inner.DatabaseType;
    
    public IDatabaseClient GetDatabaseClient() => _inner.GetDatabaseClient();
    
    // Implementation would add metrics and logging around inner calls
    
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync();
    }
}
'@

# Data Types
Create-File -path (Join-Path $corePath "Data\Abstractions\DatabaseType.cs") -content @'
namespace Ghost.Core.Data.Abstractions;

/// <summary>
/// Supported database types
/// </summary>
public enum DatabaseType
{
    SQLite,
    PostgreSQL,
    InMemory
}
'@

Create-File -path (Join-Path $corePath "Data\Abstractions\CacheCapabilities.cs") -content @'
namespace Ghost.Core.Data.Abstractions;

/// <summary>
/// Describes the capabilities of a cache provider
/// </summary>
public class CacheCapabilities
{
    public bool SupportsDistributedLock { get; init; }
    public bool SupportsAtomicOperations { get; init; } 
    public bool SupportsPubSub { get; init; }
    public bool SupportsTagging { get; init; }
    public long MaxKeySize { get; init; }
    public long MaxValueSize { get; init; }
}
'@

Create-File -path (Join-Path $corePath "Data\Abstractions\ColumnInfo.cs") -content @'
namespace Ghost.Core.Data.Abstractions;

/// <summary>
/// Information about a database column
/// </summary>
public class ColumnInfo
{
    public ColumnInfo(
        string name, 
        string type, 
        bool isNullable, 
        bool isPrimaryKey, 
        bool isAutoIncrement, 
        string? defaultValue = null)
    {
        Name = name;
        Type = type;
        IsNullable = isNullable;
        IsPrimaryKey = isPrimaryKey;
        IsAutoIncrement = isAutoIncrement;
        DefaultValue = defaultValue;
    }

    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsAutoIncrement { get; set; }
    public string? DefaultValue { get; set; }
}
'@

Create-File -path (Join-Path $corePath "Data\Abstractions\IndexInfo.cs") -content @'
namespace Ghost.Core.Data.Abstractions;

/// <summary>
/// Information about a database index
/// </summary>
public class IndexInfo
{
    public string Name { get; set; } = string.Empty;
    public string[] Columns { get; set; } = Array.Empty<string>();
    public bool IsUnique { get; set; }
    public string? Filter { get; set; }
}
'@

# Connection Pool Management
Create-File -path (Join-Path $corePath "Pooling\ConnectionPoolManager.cs") -content @'
namespace Ghost.Core.Pooling;

using Ghost.Core.Configuration;
using Microsoft.Extensions.Options;
using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Manages database connection pools for improved performance
/// </summary>
public sealed class ConnectionPoolManager : IConnectionPoolManager, IAsyncDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _redisPool;
    private readonly NpgsqlDataSource? _pgDataSource;
    private readonly SemaphoreSlim _redisSemaphore = new(1, 1);
    private readonly GhostConfiguration _config;
    private bool _disposed;
    
    public ConnectionPoolManager(IOptions<GhostConfiguration> config)
    {
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
        
        // Initialize Redis pool lazily
        _redisPool = new Lazy<ConnectionMultiplexer>(() => 
            ConnectionMultiplexer.Connect(_config.Redis.ConnectionString));
            
        // Initialize Postgres pool if configured
        if (!string.IsNullOrEmpty(_config.Postgres.ConnectionString))
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(_config.Postgres.ConnectionString);
            dataSourceBuilder.MaxPoolSize = _config.Postgres.MaxPoolSize;
            _pgDataSource = dataSourceBuilder.Build();
        }
    }
    
    public async Task<IDatabase> GetRedisDatabaseAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPoolManager));
        
        await _redisSemaphore.WaitAsync();
        try
        {
            return _redisPool.Value.GetDatabase();
        }
        finally
        {
            _redisSemaphore.Release();
        }
    }
    
    public async Task<NpgsqlConnection> GetPostgresConnectionAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ConnectionPoolManager));
        if (_pgDataSource == null) throw new InvalidOperationException("PostgreSQL is not configured");
        
        var connection = await _pgDataSource.OpenConnectionAsync();
        return connection;
    }
    
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        if (_redisPool.IsValueCreated)
        {
            await _redisPool.Value.CloseAsync();
            _redisPool.Value.Dispose();
        }
        
        _pgDataSource?.Dispose();
        _redisSemaphore.Dispose();
    }
}
'@

# Enhanced Configuration
Create-File -path (Join-Path $corePath "Configuration\GhostConfiguration.cs") -content @'
namespace Ghost.Core.Configuration;

/// <summary>
/// Root configuration class for Ghost applications
/// </summary>
public class GhostConfiguration
{
    public AppInfo App { get; set; } = new();
    public CoreConfig Core { get; set; } = new();
    public RedisConfiguration Redis { get; set; } = new();
    public PostgresConfiguration Postgres { get; set; } = new();
    public CachingConfiguration Caching { get; set; } = new();
    public ResilienceConfiguration Resilience { get; set; } = new();
    public SecurityConfiguration Security { get; set; } = new();
    public ObservabilityConfiguration Observability { get; set; } = new();
    public Dictionary<string, ModuleConfig> Modules { get; set; } = new();
    
    public bool HasModule(string name) => 
        Modules.ContainsKey(name) && Modules[name].Enabled;
        
    public T? GetModuleConfig<T>(string name) where T : ModuleConfig => 
        HasModule(name) ? (T)Modules[name] : null;
}

public class RedisConfiguration
{
    public string ConnectionString { get; set; } = "localhost:6379";
    public int Database { get; set; } = 0;
    public bool UseSsl { get; set; } = false;
    public int MaxRetries { get; set; } = 3;
}

public class PostgresConfiguration
{
    public string ConnectionString { get; set; } = "Host=localhost;Database=ghost;";
    public int MaxPoolSize { get; set; } = 100;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public class CachingConfiguration
{
    public bool EnableL1Cache { get; set; } = true;
    public bool EnableL2Cache { get; set; } = true;
    public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxCacheItems { get; set; } = 10000;
    public string Path { get; set; } = "cache";
    public long MaxSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
}

public class ResilienceConfiguration
{
    public bool EnableRetry { get; set; } = true;
    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    
    public bool EnableCircuitBreaker { get; set; } = true;
    public int FailureThreshold { get; set; } = 5;
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(1);
    
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public class SecurityConfiguration
{
    public bool EnableEncryption { get; set; } = false;
    public string EncryptionKey { get; set; } = "";
    public bool EnableAudit { get; set; } = false;
}

public class ObservabilityConfiguration
{
    public string LogLevel { get; set; } = "Information";
    public bool EnableConsoleLogging { get; set; } = true;
    public bool EnableFileLogging { get; set; } = true;
    public bool EnableMetrics { get; set; } = true;
    public bool EnableTracing { get; set; } = false;
    public string LogsPath { get; set; } = "logs";
    public int RetentionDays { get; set; } = 7;
}
'@

# Enhanced Message Bus
Create-File -path (Join-Path $corePath "Messaging\IGhostBus.cs") -content @'
namespace Ghost.Core.Messaging;

/// <summary>
/// Enhanced message bus interface with support for various messaging patterns
/// </summary>
public interface IGhostBus : IAsyncDisposable
{
    // Standard pub/sub with optional delivery guarantee
    Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null) where T : class;
    IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, CancellationToken ct = default) where T : class;
    
    // Request/response pattern
    Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string channel, 
        TRequest request, 
        TimeSpan timeout)
        where TRequest : class
        where TResponse : class;
    
    // Persistent messaging
    Task PublishPersistentAsync<T>(
        string channel, 
        T message, 
        TimeSpan? retention = null) where T : class;
    
    // Typed subscriptions with handlers
    IAsyncDisposable Subscribe<T>(
        string channel, 
        Func<T, CancellationToken, Task> handler) where T : class;
        
    // Unsubscribe from a channel
    Task UnsubscribeAsync(string channelPattern);
    
    // Check availability
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    
    // Get the channel name from the last received message
    string GetLastTopic();
}
'@

# Migration helper script - improved version
Create-File -path (Join-Path $basePath "migrate-imports.ps1") -content @'
# Migration Helper Script for Ghost Architecture Restructuring
# Run this after the restructuring to update import statements in existing files

$stats = @{
    "FilesUpdated" = 0
    "References" = @{
        "Namespaces" = 0
        "Types" = 0
    }
}

Write-Host "Starting import statement migration..." -ForegroundColor Cyan

$files = Get-ChildItem -Path . -Include *.cs -Recurse

foreach ($file in $files) {
    $originalContent = Get-Content $file.FullName -Raw
    $content = $originalContent
    $namespaceChanges = 0
    $typeChanges = 0
    
    # Update namespaces
    $namespaceMapping = @{
        'using Ghost.Core.Data;' = 'using Ghost.Core.Data.Interfaces;'
        'using Ghost.Core.Storage;' = 'using Ghost.Core.Messaging;'
        'using Ghost.Core.Monitoring;' = 'using Ghost.Core.Observability;'
    }
    
    foreach ($oldNs in $namespaceMapping.Keys) {
        $newNs = $namespaceMapping[$oldNs]
        $oldCount = ($content | Select-String -Pattern ([regex]::Escape($oldNs)) -AllMatches).Matches.Count
        $content = $content -replace ([regex]::Escape($oldNs)), $newNs
        $newCount = ($content | Select-String -Pattern ([regex]::Escape($newNs)) -AllMatches).Matches.Count
        $namespaceChanges += ($newCount - $oldCount + $oldCount)
    }
    
    # Update interface and class references
    $typeMapping = @{
        'ICache\b' = 'ICacheProvider'
        'LocalCache\b' = 'LocalCacheProvider'
        'GhostBus\b' = 'RedisBus'
        'MetricsCollector\b' = 'MetricsService'
    }
    
    foreach ($oldType in $typeMapping.Keys) {
        $newType = $typeMapping[$oldType]
        $oldCount = ($content | Select-String -Pattern $oldType -AllMatches).Matches.Count
        $content = $content -replace $oldType, $newType
        $newCount = ($content | Select-String -Pattern $newType -AllMatches).Matches.Count
        $typeChanges += ($newCount - $oldCount + $oldCount)
    }
    
    # Add additional imports if needed
    if ($content.Contains("ICacheProvider") -and -not $content.Contains("using Ghost.Core.Data.Interfaces;")) {
        $content = "using Ghost.Core.Data.Interfaces;`r`n" + $content
        $namespaceChanges++
    }
    
    if ($content.Contains("RedisBus") -and -not $content.Contains("using Ghost.Core.Messaging;")) {
        $content = "using Ghost.Core.Messaging;`r`n" + $content
        $namespaceChanges++
    }
    
    # Save changes if content was modified
    if ($content -ne $originalContent) {
        Set-Content -Path $file.FullName -Value $content
        Write-Host "Updated: $($file.FullName)" -ForegroundColor Green
        $stats.FilesUpdated++
        $stats.References.Namespaces += $namespaceChanges
        $stats.References.Types += $typeChanges
    }
}

# Print summary
Write-Host "`nMigration completed!" -ForegroundColor Green
Write-Host "Files updated: $($stats.FilesUpdated)" -ForegroundColor Cyan
Write-Host "Namespace references updated: $($stats.References.Namespaces)" -ForegroundColor Cyan
Write-Host "Type references updated: $($stats.References.Types)" -ForegroundColor Cyan
'@

# STEP 4: Remove additional obsolete files
Write-Host "`nRemoving additional obsolete files..." -ForegroundColor Cyan

# Storage folder cleanup
$storageFolder = Join-Path $corePath "Storage"
Remove-DirectoryIfExists -path $storageFolder -backup

# STEP 5: Cleanup other legacy files from other components that rely on old interfaces
Write-Host "`nCleaning up related components..." -ForegroundColor Cyan

# Remove PostgreSQL old implementation files
Remove-FileIfExists -path (Join-Path $postgresPath "GhostDatabaseInitializer.cs") -backup
Remove-FileIfExists -path (Join-Path $postgresPath "PostgresDatabase.cs") -backup
Remove-FileIfExists -path (Join-Path $postgresPath "PostgresTransactionWrapper.cs") -backup
Remove-FileIfExists -path (Join-Path $postgresPath "RedisCache.cs") -backup

# Display summary of changes
Write-Host "`nRestructuring complete!" -ForegroundColor Green
Write-Host "Summary of changes:" -ForegroundColor Cyan
Write-Host "- Directories created: $($stats.DirectoriesCreated)" -ForegroundColor Gray
Write-Host "- Directories removed: $($stats.DirectoriesRemoved)" -ForegroundColor Gray
Write-Host "- Files created: $($stats.FilesCreated)" -ForegroundColor Gray  
Write-Host "- Files removed: $($stats.FilesRemoved)" -ForegroundColor Gray
Write-Host "- Files backed up: $($stats.FilesBackedUp)" -ForegroundColor Gray
Write-Host "`nBackup created at: $backupDir" -ForegroundColor Yellow
Write-Host "Run .\migrate-imports.ps1 to update import statements in existing files." -ForegroundColor Yellow