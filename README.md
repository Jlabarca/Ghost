# GhostFather 👻

A modern .NET-based application orchestration framework for building, deploying, and managing distributed applications with ease.

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![.NET](https://img.shields.io/badge/.NET-8.0-purple.svg)
![Stage](https://img.shields.io/badge/stage-alpha-orange.svg)

## 🌟 Features

- **Process Orchestration**: Sophisticated process management and supervision with automatic recovery
- **Dual-Mode Operation**: Run locally with SQLite/in-memory or distributed with PostgreSQL/Redis
- **Health Monitoring**: Built-in health checks, metrics collection, and process monitoring
- **Configuration Management**: Hot-reloadable YAML configuration system
- **Template System**: Bootstrap new applications quickly with customizable templates
- **CLI Interface**: Powerful command-line tools for application management
- **Structured Logging**: Comprehensive logging with file rotation and Redis integration
- **Distributed Communication**: Built-in message bus pattern for inter-process communication

## 🏗️ Architecture
# GhostFather Architecture Overview

GhostFather is a comprehensive .NET application management framework that provides process monitoring, lifecycle management, and tooling for both .NET applications and external processes. It combines elements of PM2 (Node.js process manager) with .NET-specific capabilities and a rich SDK for application development.

## Core Architecture

GhostFather follows a daemon-based architecture with a central process (`GhostFatherDaemon`) that manages and monitors child processes, combined with a CLI interface for user interaction. The system uses a message bus for inter-process communication and a robust storage layer for persistence.

### Key Components

1. **GhostFatherDaemon**: Central process manager that handles process registration, monitoring, and lifecycle events
2. **GhostCLI**: Command-line interface for interacting with GhostFather
3. **GhostApp**: Base class for Ghost-aware applications
4. **GhostBus**: Enhanced message bus for inter-process communication
5. **Ghost Static API**: Simplified access to core services

## Folder Structure

```
[GHOST_INSTALL]/
  ├── bin/                 # Binaries and executables
  ├── ghosts/              # Ghost applications folder
  │   ├── app1/            # Individual Ghost application
  │   │   ├── .ghost.yaml  # Ghost configuration
  │   │   └── ...          # Application files
  │   └── app2/
  │       └── ...
  ├── templates/           # Project templates
  ├── logs/                # Log files
  └── data/                # Persistent data storage
```

## The Ghost Static API

The framework provides a simplified static API for accessing core services:

```csharp
// Initialize Ghost (done automatically when inheriting GhostApp)
Ghost.Init(app);

// Access metrics
await Ghost.Metrics.TrackAsync("app.started", 1);

// Access configuration
string appName = Ghost.Config.App.Name;

// Use data storage
await Ghost.Data.ExecuteAsync("CREATE TABLE IF NOT EXISTS items (id TEXT, name TEXT)");

// Access message bus
await Ghost.Bus.PublishAsync("app:events", new { Type = "AppStarted" });

// Logging (via G static class)
G.LogInfo("Application started");
```

## GhostApp Base Class

The `GhostApp` class serves as the foundation for Ghost-aware applications, providing automatic integration with the monitoring system:

```csharp
public class MyApp : GhostApp
{
    public override async Task RunAsync(IEnumerable<string> args)
    {
        G.LogInfo("MyApp is running");
        
        // Access Ghost services
        await Ghost.Metrics.TrackAsync("custom.metric", 42);
        
        // Your application logic here
        while (!CancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000);
        }
    }
    
    protected override async Task OnTickAsync()
    {
        // Called periodically for service applications
        await Task.CompletedTask;
    }
}
```

### Configuration via .ghost.yaml

Applications can be configured using a `.ghost.yaml` file:

```yaml
app:
  id: my-app
  name: My Application
  description: A sample Ghost application
  version: 1.0.0

core:
  mode: development
  logsPath: logs
  dataPath: data
  settings:
    autoGhostFather: true
    autoMonitor: true
    type: service
```

## Process Management and Orchestration

GhostFather includes a powerful process orchestration system that provides advanced lifecycle management for both Ghost apps and external processes.

### Process Groups with Dependencies

```csharp
public interface IProcessOrchestrator
{
    Task<ProcessGroup> CreateGroupAsync(string name, ProcessGroupConfig config);
    Task StartGroupAsync(string groupId, StartupOrder order = StartupOrder.Dependency);
    Task StopGroupAsync(string groupId, bool graceful = true);
    Task<ProcessGroupStatus> GetGroupStatusAsync(string groupId);
}

public enum StartupOrder
{
    Parallel,      // Start all at once
    Sequential,    // Start one by one
    Dependency     // Start based on dependency graph
}
```

### Health-Based Orchestration

The system integrates health checks into the orchestration process, ensuring processes are fully initialized before dependent processes start:

```csharp
private async Task StartProcessWithHealthCheckAsync(string processId)
{
    await _processManager.StartProcessAsync(processId);
    
    // Wait for process to become healthy
    await _healthMonitor.WaitForHealthyAsync(processId, timeout: TimeSpan.FromMinutes(2));
}
```

## Enhanced Data Architecture

The data layer follows a decorator pattern, allowing for a layered approach to data access with separation of concerns:

```csharp
public interface IGhostData : IAsyncDisposable
{
    // Single operations
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    
    // Batch operations for performance
    Task<IDictionary<string, T?>> GetBatchAsync<T>(IEnumerable<string> keys, CancellationToken ct = default);
    Task SetBatchAsync<T>(IDictionary<string, T> items, TimeSpan? expiry = null, CancellationToken ct = default);
    Task<int> DeleteBatchAsync(IEnumerable<string> keys, CancellationToken ct = default);
    
    // SQL operations
    Task<T> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default);
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default);
    Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default);
    Task<int> ExecuteBatchAsync(IEnumerable<(string sql, object? param)> commands, CancellationToken ct = default);
    
    // Transaction support
    Task<IGhostTransaction> BeginTransactionAsync(CancellationToken ct = default);
}
```

### Decorator Pattern Implementation

Each decorator adds a specific concern to the data layer:

```csharp
// Core implementation
public class CoreGhostData : IGhostData { /* Implementation */ }

// Decorator chain
public class EncryptedGhostData : IGhostData
{
    private readonly IGhostData _inner;
    private readonly IDataProtector _protector;
    
    // Implementation focuses on encryption concerns
}

public class CachedGhostData : IGhostData { /* L1 cache implementation */ }
public class ResilientGhostData : IGhostData { /* Retry and circuit breaker */ }
public class InstrumentedGhostData : IGhostData { /* Metrics and tracing */ }
```

## Multi-Level Caching Strategy

The system implements a sophisticated multi-level caching strategy:

### L1 Memory Cache + L2 Redis Cache

```csharp
public class CachedGhostData : IGhostData
{
    private readonly IGhostData _inner;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptions<CachingConfiguration> _config;
    
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        // L1 cache check
        if (_memoryCache.TryGetValue(key, out T? cachedValue))
            return cachedValue;
        
        // L2 cache + database fallback
        var value = await _inner.GetAsync<T>(key, ct);
        
        if (value != null)
        {
            _memoryCache.Set(key, value, _config.Value.DefaultExpiration);
        }
        
        return value;
    }
}
```

### Cache Invalidation

```csharp
public class CacheInvalidator
{
    public async Task InvalidateKeyAsync(string key)
    {
        // Remove from L1 cache
        _memoryCache.Remove(key);
        
        // Remove from L2 cache
        await _redis.KeyDeleteAsync(key);
        
        // Notify other instances
        await _bus.PublishAsync("cache:invalidation", key);
    }
    
    public async Task InvalidatePatternAsync(string pattern)
    {
        // Pattern-based invalidation with notification
        var keys = await _redis.SearchKeysAsync(pattern);
        await InvalidateBatchAsync(keys);
    }
}
```

## Enhanced Communication Layer

### Advanced Message Bus

The message bus provides sophisticated communication patterns:

```csharp
public interface IGhostBus : IAsyncDisposable
{
    // Standard pub/sub with guaranteed delivery
    Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null) where T : class;
    IAsyncEnumerable<T> SubscribeAsync<T>(string channel, CancellationToken ct = default) where T : class;
    
    // Request/response pattern
    Task<TResponse> RequestAsync<TRequest, TResponse>(
        string channel, 
        TRequest request, 
        TimeSpan timeout)
        where TRequest : class
        where TResponse : class;
    
    // Persistent messaging
    Task PublishPersistentAsync<T>(string channel, T message, TimeSpan? retention = null) where T : class;
    
    // Typed subscriptions with handlers
    IAsyncDisposable Subscribe<T>(string channel, Func<T, CancellationToken, Task> handler) where T : class;
}
```

### Message Patterns

```csharp
// Direct request/response
var response = await bus.RequestAsync<UserQuery, UserResponse>(
    "user:query", 
    new UserQuery { Id = userId }, 
    TimeSpan.FromSeconds(5));

// Persistent messaging for critical events
await bus.PublishPersistentAsync("audit:log", new AuditEvent
{
    Id = Guid.NewGuid(),
    Action = "user_created",
    Timestamp = DateTime.UtcNow
}, retention: TimeSpan.FromDays(30));

// Typed subscription with auto-deserialization
var subscription = bus.Subscribe<ProcessMetrics>("metrics:*", async (metrics, ct) =>
{
    await metricsCollector.RecordAsync(metrics);
});
```

## Resilience Patterns

### Retry and Circuit Breaker

```csharp
public class ResilientGhostData : IGhostData
{
    private readonly IAsyncPolicy _retryPolicy;
    private readonly IAsyncPolicy _circuitBreakerPolicy;
    
    public ResilientGhostData(IGhostData inner, IOptions<ResilienceConfiguration> config)
    {
        _retryPolicy = Policy
            .Handle<RedisException>()
            .Or<NpgsqlException>()
            .WaitAndRetryAsync(
                config.Value.RetryCount,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    _logger.LogWarning(exception, 
                        "Retry {RetryCount} after {Delay}ms", retryCount, timeSpan.TotalMilliseconds);
                });
        
        _circuitBreakerPolicy = Policy
            .Handle<Exception>()
            .CircuitBreakerAsync(
                config.Value.CircuitBreakerThreshold,
                config.Value.CircuitBreakerDuration,
                onBreak: (exception, duration) =>
                {
                    _logger.LogError(exception, 
                        "Circuit breaker opened for {Duration}s", duration.TotalSeconds);
                },
                onReset: () => _logger.LogInformation("Circuit breaker reset"));
    }
}
```

### Distributed Transactions (Saga Pattern)

```csharp
public interface ISagaCoordinator
{
    Task<SagaResult> ExecuteAsync(Func<ISagaContext, Task> sagaDefinition);
}

public class SagaCoordinator : ISagaCoordinator
{
    public async Task<SagaResult> ExecuteAsync(Func<ISagaContext, Task> sagaDefinition)
    {
        var context = new SagaContext();
        try
        {
            await sagaDefinition(context);
            return SagaResult.Success();
        }
        catch (Exception ex)
        {
            // Execute compensations in reverse order
            foreach (var compensation in context.Compensations.Reverse())
            {
                try
                {
                    await compensation();
                }
                catch (Exception compEx)
                {
                    _logger.LogError(compEx, "Compensation failed");
                }
            }
            return SagaResult.Failed(ex);
        }
    }
}
```

## Observability

### Instrumentation Layer

```csharp
public class InstrumentedGhostData : IGhostData
{
    private readonly IGhostData _inner;
    private readonly IMetricsCollector _metrics;
    private readonly ILogger<InstrumentedGhostData> _logger;
    
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        using var activity = Activity.StartActivity("GhostData.GetAsync");
        activity?.SetTag("key", key);
        activity?.SetTag("type", typeof(T).Name);
        
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.GetAsync<T>(key, ct);
            
            _metrics.RecordLatency("ghost.data.get", sw.ElapsedMilliseconds);
            _metrics.IncrementCounter("ghost.data.get.success");
            
            activity?.SetTag("cache_hit", result != null);
            return result;
        }
        catch (Exception ex)
        {
            _metrics.IncrementCounter("ghost.data.get.error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Failed to get key {Key}", key);
            throw;
        }
    }
}
```

### Health Checks

```csharp
public class GhostHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken ct = default)
    {
        var data = new Dictionary<string, object>();
        
        try
        {
            // Check Redis
            var redisLatency = await MeasureLatencyAsync(() => 
                _data.SetAsync("health:check", true, TimeSpan.FromSeconds(1), ct));
            data["redis_latency_ms"] = redisLatency;
            
            // Check PostgreSQL
            var pgLatency = await MeasureLatencyAsync(() => 
                _data.QuerySingleAsync<int>("SELECT 1", cancellationToken: ct));
            data["postgres_latency_ms"] = pgLatency;
            
            return HealthCheckResult.Healthy("All systems operational", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("System check failed", ex, data);
        }
    }
}
```

## Command Line Interface

GhostFather provides a comprehensive CLI:

```
# Create a new Ghost app
ghost create [name] [--template basic]

# Run a Ghost app
ghost run [name] [--watch] [--args "arg1 arg2"]

# Monitor running apps
ghost monitor [--processId id]

# List all apps
ghost list

# Start/stop/restart
ghost start [name]
ghost stop [name]
ghost restart [name]

# Register external processes
ghost register [configPath]
```

## PM2-like Process Management Example

This example shows how to use the PM2-like process management capabilities:

```yaml
# processes.yaml
processes:
  - name: redis
    executable: redis-server
    restart: true
    
  - name: python-script
    executable: python
    args: app.py --port 8000
    working_directory: /services/analytics
    environment:
      PYTHONPATH: /lib/python
    watch:
      enabled: true
      paths: ["*.py"]
    
  - name: nginx
    executable: /usr/sbin/nginx
    args: -c /etc/nginx/nginx.conf
    restart: true
    resource_limits:
      memory: 512  # MB
```

Register and manage these processes:

```bash
# Register processes
ghost register processes.yaml

# Start a specific process
ghost start redis

# Check status
ghost status

# Monitor all processes
ghost monitor
```

The monitor will show a dashboard with both Ghost apps and external processes:

```
                     System Status
┌────────────┬─────────────┬──────────────────────────┐
│ Component  │ Status      │ Info                     │
├────────────┼─────────────┼──────────────────────────┤
│ CPU        │ 2.1%        │ Threads: 24              │
│ Memory     │ 128.74MB    │ Private: 84.44MB         │
│ GC         │ Gen0: 3     │ Gen1: 1, Gen2: 0         │
│ Monitoring │ 5 processes │ Services: 3, One-shot: 2 │
└────────────┴─────────────┴──────────────────────────┘
                             Services
┌─────────────┬─────────┬──────────────┬──────────┬────────────────┬─────────┐
│ Service     │ Status  │ Started      │ Stopped  │ Resource Usage │ Actions │
├─────────────┼─────────┼──────────────┼──────────┼────────────────┼─────────┤
│ redis       │ Running │ 12:30:45     │          │ CPU: 0.2%      │ restart │
│             │         │              │          │ MEM: 24.5MB    │  stop   │
├─────────────┼─────────┼──────────────┼──────────┼────────────────┼─────────┤
│ nginx       │ Running │ 12:31:22     │          │ CPU: 0.1%      │ restart │
│             │         │              │          │ MEM: 18.2MB    │  stop   │
├─────────────┼─────────┼──────────────┼──────────┼────────────────┼─────────┤
│ ghost-father│ Running │ 12:30:00     │          │ CPU: 0.5%      │         │
│             │         │              │          │ MEM: 42.1MB    │         │
└─────────────┴─────────┴──────────────┴──────────┴────────────────┴─────────┘
                 One-Shot Applications
┌──────────────┬─────────┬──────────────┬────────────┬────────────────┐
│ App          │ Status  │ Started      │ Completed  │ Resource Usage │
├──────────────┼─────────┼──────────────┼────────────┼────────────────┤
│ python-script│ Running │ 13:05:22     │            │ CPU: 1.2%      │
│              │         │              │            │ MEM: 45.7MB    │
├──────────────┼─────────┼──────────────┼────────────┼────────────────┤
│ backup-task  │ Stopped │ 13:00:10     │ 13:01:45   │ CPU: 0.0%      │
│              │         │              │            │ MEM: 0.0MB     │
└──────────────┴─────────┴──────────────┴────────────┴────────────────┘
```

## Testing Support

The architecture includes comprehensive testing support:

```csharp
public class InMemoryGhostData : IGhostData
{
    private readonly ConcurrentDictionary<string, object> _store = new();
    private readonly ConcurrentDictionary<string, DateTime> _expiry = new();
    
    public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (_expiry.TryGetValue(key, out var expiryTime) && DateTime.UtcNow > expiryTime)
        {
            _store.TryRemove(key, out _);
            _expiry.TryRemove(key, out _);
            return Task.FromResult<T?>(default);
        }
        
        return Task.FromResult(_store.TryGetValue(key, out var value) ? (T)value : default);
    }
}
```

### Testing Extensions

```csharp
public static class TestingExtensions
{
    public static IServiceCollection AddGhostDataLayerForTesting(
        this IServiceCollection services)
    {
        services.AddSingleton<IGhostData, InMemoryGhostData>();
        services.AddSingleton<IGhostBus, InMemoryMessageBus>();
        services.AddSingleton<IProcessOrchestrator, MockProcessOrchestrator>();
        
        return services;
    }
}
```

## Architecture and Design Principles

1. **Decorator Pattern**: Layered functionality with separation of concerns
2. **Resilience First**: Built-in retry policies, circuit breakers, and fallback mechanisms
3. **Multi-Level Caching**: In-memory L1 cache backed by Redis L2 cache
4. **Strongly-Typed Configuration**: Type-safe configuration with validation
5. **Distributed Transactions**: Saga pattern for cross-store consistency
6. **Observable Systems**: Metrics, structured logging, and distributed tracing
7. **Testing Support**: In-memory implementations for fast, reliable tests
8. **Automatic Discovery**: Ghost apps are automatically discovered in the `ghosts/` folder
9. **Self-Healing**: Automatic restart and recovery for failed processes
10. **Resource Monitoring**: Comprehensive monitoring of process resources
## 🚀 Quick Start

1. Install the Ghost CLI:
```bash
dotnet tool install --global Ghost
```

2. Create a new Ghost application:
```bash
ghost create MyApp
```

3. Run your application:
```bash
ghost run MyApp
```

## 💻 Development Setup

Requirements:
- .NET 8.0 SDK
- Git
- Redis (optional, for distributed mode)
- PostgreSQL (optional, for distributed mode)

Build and install locally:
```bash
# Clone repository
git clone https://github.com/yourusername/ghost.git
cd ghost

# Build project
dotnet build

# Create NuGet package
dotnet pack

# Install locally
dotnet tool install --global --add-source ./nupkg Ghost
```

## 🛠️ Configuration

Ghost uses YAML for configuration. Create a `.ghost.yaml` in your project root:

```yaml
system:
  id: my-app
  mode: local  # or 'distributed'

storage:
  redis:
    enabled: false
    connection: "localhost:6379"
  postgres:
    enabled: false
    connection: "Host=localhost;Database=ghost;"

monitoring:
  enabled: true
  interval: "00:00:05"
```

## 📊 Process Management

GhostFather provides sophisticated process management:

- Automatic process recovery
- Health monitoring
- Resource usage tracking
- State persistence
- Inter-process communication

Monitor your processes:
```bash
ghost monitor MyApp
```

## 🔌 SDK Usage

Create a simple one-off Ghost application:

```csharp
public class MyApp : GhostApp
{
    public override async Task RunAsync()
    {
        // Your application logic here
        await Task.Delay(1000);
        Ghost.LogInfo("Hello from MyApp!");
    }
}
```

Create a long-running Ghost service:

```csharp
public class MyService : GhostApp
{
    public MyService()
    {
        // Configure as service
        IsService = true;
        TickInterval = TimeSpan.FromSeconds(1);
        AutoRestart = true;
    }

    public override async Task RunAsync()
    {
        // Initial setup
        Ghost.LogInfo("Service starting...");
    }

    protected override async Task OnTickAsync()
    {
        // Regular service work
        Ghost.LogInfo("Service heartbeat");
        await ProcessWorkItems();
    }

    protected override async Task OnAfterRunAsync()
    {
        // Cleanup
        Ghost.LogInfo("Service shutting down...");
    }
}
```

## 📦 Templates

Ghost includes built-in templates for common application patterns:

- Basic console application
- Long-running service
- Web API
- Worker service

Create from template:
```bash
ghost create MyApp --template service
```

## 📝 Logging

Ghost provides structured logging with multiple outputs:

- Console logging
- File logging with rotation
- Redis logging (distributed mode)
- Metrics collection

## 🤝 Contributing

We welcome contributions! Please read our [Contributing Guidelines](CONTRIBUTING.md) before submitting PRs.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.