using System;
using System.Collections.Generic;

namespace Ghost.Core.Configuration
{
    /// <summary>
    /// Root configuration class for Ghost applications
    /// </summary>
    public class GhostConfiguration
    {
        /// <summary>
        /// Application information
        /// </summary>
        public AppInfo App { get; set; } = new();
        
        /// <summary>
        /// Core configuration
        /// </summary>
        public CoreConfig Core { get; set; } = new();
        
        /// <summary>
        /// Redis configuration
        /// </summary>
        public RedisConfiguration Redis { get; set; } = new();
        
        /// <summary>
        /// PostgreSQL configuration
        /// </summary>
        public PostgresConfiguration Postgres { get; set; } = new();
        
        /// <summary>
        /// Caching configuration
        /// </summary>
        public CachingConfiguration Caching { get; set; } = new();
        
        /// <summary>
        /// Resilience configuration
        /// </summary>
        public ResilienceConfiguration Resilience { get; set; } = new();
        
        /// <summary>
        /// Security configuration
        /// </summary>
        public SecurityConfiguration Security { get; set; } = new();
        
        /// <summary>
        /// Observability configuration
        /// </summary>
        public ObservabilityConfiguration Observability { get; set; } = new();
        
        /// <summary>
        /// Module-specific configurations
        /// </summary>
        public Dictionary<string, ModuleConfig> Modules { get; set; } = new();
        
        /// <summary>
        /// Checks if a module is enabled
        /// </summary>
        /// <param name="name">The name of the module</param>
        /// <returns>True if the module is enabled; otherwise, false</returns>
        public bool HasModule(string name) => 
            Modules.ContainsKey(name) && Modules[name].Enabled;
        
        /// <summary>
        /// Gets the configuration for a module
        /// </summary>
        /// <typeparam name="T">The type of the module configuration</typeparam>
        /// <param name="name">The name of the module</param>
        /// <returns>The module configuration if the module is enabled; otherwise, null</returns>
        public T? GetModuleConfig<T>(string name) where T : ModuleConfig => 
            HasModule(name) ? (T)Modules[name] : null;
    }

    /// <summary>
    /// Application information configuration
    /// </summary>
    public class AppInfo
    {
        /// <summary>
        /// The application ID
        /// </summary>
        public string Id { get; set; } = "ghost-app";
        
        /// <summary>
        /// The application display name
        /// </summary>
        public string Name { get; set; } = "Ghost Application";
        
        /// <summary>
        /// The application description
        /// </summary>
        public string Description { get; set; } = "A Ghost Application";
        
        /// <summary>
        /// The application version
        /// </summary>
        public string Version { get; set; } = "1.0.0";
    }

    /// <summary>
    /// Core configuration for Ghost applications
    /// </summary>
    public class CoreConfig
    {
        /// <summary>
        /// The application environment (development, production, etc.)
        /// </summary>
        public string Environment { get; set; } = "development";
        
        /// <summary>
        /// The path to store logs
        /// </summary>
        public string LogsPath { get; set; } = "logs";
        
        /// <summary>
        /// The path to store data
        /// </summary>
        public string DataPath { get; set; } = "data";
        
        /// <summary>
        /// Whether to enable automatic monitoring
        /// </summary>
        public bool AutoMonitor { get; set; } = true;
        
        /// <summary>
        /// Whether to enable automatic daemon registration
        /// </summary>
        public bool AutoDaemon { get; set; } = true;
        
        /// <summary>
        /// The application type (service, one-shot, etc.)
        /// </summary>
        public string Type { get; set; } = "service";
    }

    /// <summary>
    /// Redis configuration for Ghost applications
    /// </summary>
    public class RedisConfiguration
    {
        /// <summary>
        /// The Redis connection string
        /// </summary>
        public string ConnectionString { get; set; } = "localhost:6379";
        
        /// <summary>
        /// The Redis database number
        /// </summary>
        public int Database { get; set; } = 0;
        
        /// <summary>
        /// Whether to use SSL for Redis connections
        /// </summary>
        public bool UseSsl { get; set; } = false;
        
        /// <summary>
        /// Maximum number of connection retries
        /// </summary>
        public int ConnectRetry { get; set; } = 3;
        
        /// <summary>
        /// The connection timeout
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
        
        /// <summary>
        /// The synchronous operation timeout
        /// </summary>
        public TimeSpan SyncTimeout { get; set; } = TimeSpan.FromSeconds(5);
        
        /// <summary>
        /// The command execution timeout
        /// </summary>
        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);
        
        /// <summary>
        /// The key prefix for this application
        /// </summary>
        public string KeyPrefix { get; set; } = "";
    }

    /// <summary>
    /// PostgreSQL configuration for Ghost applications
    /// </summary>
    public class PostgresConfiguration
    {
        /// <summary>
        /// The PostgreSQL connection string
        /// </summary>
        public string ConnectionString { get; set; } = "Host=localhost;Database=ghost;";
        
        /// <summary>
        /// The maximum connection pool size
        /// </summary>
        public int MaxPoolSize { get; set; } = 100;
        
        /// <summary>
        /// The minimum connection pool size
        /// </summary>
        public int MinPoolSize { get; set; } = 5;
        
        /// <summary>
        /// Whether to prewarm the connection pool
        /// </summary>
        public bool PrewarmConnections { get; set; } = false;
        
        /// <summary>
        /// The connection lifetime
        /// </summary>
        public TimeSpan ConnectionLifetime { get; set; } = TimeSpan.FromMinutes(30);
        
        /// <summary>
        /// The idle connection lifetime
        /// </summary>
        public TimeSpan ConnectionIdleLifetime { get; set; } = TimeSpan.FromMinutes(5);
        
        /// <summary>
        /// The command execution timeout
        /// </summary>
        public int CommandTimeout { get; set; } = 30;
        
        /// <summary>
        /// Whether to enable parameter logging
        /// </summary>
        public bool EnableParameterLogging { get; set; } = false;
        
        /// <summary>
        /// The schema name
        /// </summary>
        public string Schema { get; set; } = "public";
        
        /// <summary>
        /// Whether to enable connection pooling
        /// </summary>
        public bool EnablePooling { get; set; } = true;
    }

    /// <summary>
    /// Caching configuration for Ghost applications
    /// </summary>
    public class CachingConfiguration
    {
        /// <summary>
        /// Whether to use the L1 memory cache
        /// </summary>
        public bool UseL1Cache { get; set; } = true;
        
        /// <summary>
        /// Whether to use the L2 distributed cache
        /// </summary>
        public bool UseL2Cache { get; set; } = true;
        
        /// <summary>
        /// The default cache expiration time
        /// </summary>
        public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);
        
        /// <summary>
        /// The default sliding expiration time for L1 cache items
        /// </summary>
        public TimeSpan DefaultL1SlidingExpiration { get; set; } = TimeSpan.FromMinutes(1);
        
        /// <summary>
        /// The default absolute expiration time for L1 cache items
        /// </summary>
        public TimeSpan DefaultL1Expiration { get; set; } = TimeSpan.FromMinutes(5);
        
        /// <summary>
        /// The default absolute expiration time for L2 cache items
        /// </summary>
        public TimeSpan DefaultL2Expiration { get; set; } = TimeSpan.FromMinutes(30);
        
        /// <summary>
        /// The maximum number of items in the L1 cache
        /// </summary>
        public int MaxL1CacheItems { get; set; } = 10000;
        
        /// <summary>
        /// The maximum size of the L1 cache in bytes
        /// </summary>
        public long MaxL1CacheSizeBytes { get; set; } = 100 * 1024 * 1024; // 100MB
        
        /// <summary>
        /// The path to the persistent cache
        /// </summary>
        public string CachePath { get; set; } = "cache";
        
        /// <summary>
        /// Whether to compress cached items
        /// </summary>
        public bool CompressItems { get; set; } = false;
        
        /// <summary>
        /// The compression threshold in bytes
        /// </summary>
        public int CompressionThreshold { get; set; } = 1024; // 1KB
    }

    /// <summary>
    /// Resilience configuration for Ghost applications
    /// </summary>
    public class ResilienceConfiguration
    {
        /// <summary>
        /// Whether to enable retry policies
        /// </summary>
        public bool EnableRetry { get; set; } = true;
        
        /// <summary>
        /// The number of retry attempts
        /// </summary>
        public int RetryCount { get; set; } = 3;
        
        /// <summary>
        /// The base delay between retries in milliseconds
        /// </summary>
        public int RetryBaseDelayMs { get; set; } = 100;
        
        /// <summary>
        /// Whether to enable circuit breaker policies
        /// </summary>
        public bool EnableCircuitBreaker { get; set; } = true;
        
        /// <summary>
        /// The number of consecutive failures before opening the circuit
        /// </summary>
        public int CircuitBreakerThreshold { get; set; } = 5;
        
        /// <summary>
        /// The duration the circuit stays open before moving to half-open state in milliseconds
        /// </summary>
        public int CircuitBreakerDurationMs { get; set; } = 30000; // 30 seconds
        
        /// <summary>
        /// The default timeout for operations in milliseconds
        /// </summary>
        public int TimeoutMs { get; set; } = 30000; // 30 seconds
        
        /// <summary>
        /// Whether to enable bulkhead policies
        /// </summary>
        public bool EnableBulkhead { get; set; } = false;
        
        /// <summary>
        /// The maximum number of concurrent executions
        /// </summary>
        public int MaxConcurrency { get; set; } = 100;
        
        /// <summary>
        /// The maximum queue size for bulkhead policies
        /// </summary>
        public int MaxQueueSize { get; set; } = 50;
    }

    /// <summary>
    /// Security configuration for Ghost applications
    /// </summary>
    public class SecurityConfiguration
    {
        /// <summary>
        /// Whether to enable data encryption
        /// </summary>
        public bool EnableEncryption { get; set; } = false;
        
        /// <summary>
        /// The encryption key (Base64 encoded)
        /// </summary>
        public string EncryptionKey { get; set; } = "";
        
        /// <summary>
        /// The encryption initialization vector (Base64 encoded)
        /// </summary>
        public string EncryptionIV { get; set; } = "";
        
        /// <summary>
        /// The encryption algorithm to use
        /// </summary>
        public string EncryptionAlgorithm { get; set; } = "AES";
        
        /// <summary>
        /// Whether to enable audit logging
        /// </summary>
        public bool EnableAudit { get; set; } = false;
        
        /// <summary>
        /// Whether to enable access control
        /// </summary>
        public bool EnableAccessControl { get; set; } = false;
        
        /// <summary>
        /// The path to the security audit log
        /// </summary>
        public string AuditLogPath { get; set; } = "logs/audit";
        
        /// <summary>
        /// The audit log retention days
        /// </summary>
        public int AuditLogRetentionDays { get; set; } = 90;
    }

    /// <summary>
    /// Observability configuration for Ghost applications
    /// </summary>
    public class ObservabilityConfiguration
    {
        /// <summary>
        /// The default log level
        /// </summary>
        public string LogLevel { get; set; } = "Information";
        
        /// <summary>
        /// Whether to enable console logging
        /// </summary>
        public bool EnableConsoleLogging { get; set; } = true;
        
        /// <summary>
        /// Whether to enable file logging
        /// </summary>
        public bool EnableFileLogging { get; set; } = true;
        
        /// <summary>
        /// Whether to enable metrics collection
        /// </summary>
        public bool EnableMetrics { get; set; } = true;
        
        /// <summary>
        /// Whether to enable distributed tracing
        /// </summary>
        public bool EnableTracing { get; set; } = false;
        
        /// <summary>
        /// Whether to enable health checks
        /// </summary>
        public bool EnableHealthChecks { get; set; } = true;
        
        /// <summary>
        /// The path to store logs
        /// </summary>
        public string LogsPath { get; set; } = "logs";
        
        /// <summary>
        /// The log retention days
        /// </summary>
        public int LogRetentionDays { get; set; } = 7;
        
        /// <summary>
        /// The metrics reporting interval in seconds
        /// </summary>
        public int MetricsIntervalSeconds { get; set; } = 15;
        
        /// <summary>
        /// The health check interval in seconds
        /// </summary>
        public int HealthCheckIntervalSeconds { get; set; } = 30;
        
        /// <summary>
        /// Whether to enable structured logging
        /// </summary>
        public bool EnableStructuredLogging { get; set; } = true;
        
        /// <summary>
        /// The sampling rate for tracing (0.0-1.0)
        /// </summary>
        public double TracingSamplingRate { get; set; } = 0.1; // 10%
    }

    /// <summary>
    /// Base class for module configurations
    /// </summary>
    public class ModuleConfig
    {
        /// <summary>
        /// Whether the module is enabled
        /// </summary>
        public bool Enabled { get; set; } = true;
    }
}
