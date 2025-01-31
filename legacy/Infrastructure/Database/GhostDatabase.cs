
using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
// using Npgsql;
// using StackExchange.Redis;

namespace Ghost.Legacy.Infrastructure.Database;

// Core abstractions for database providers


public class GhostDatabase : IAsyncDisposable
{
    private readonly IDbProvider _db;
    private readonly string _appId;
    private readonly GhostLogger _logger;
    private readonly Timer _eventPollTimer;
    private readonly Dictionary<string, Func<EventEntry, Task>> _eventHandlers;
    private readonly string _tablePrefix;

    public GhostDatabase(
        IDbProvider dbProvider,
        string appId,
        GhostLogger logger)
    {
        _db = dbProvider;
        _appId = appId;
        _logger = logger;
        _tablePrefix = dbProvider.GetTablePrefix();
        _eventHandlers = new Dictionary<string, Func<EventEntry, Task>>();

        // Initialize database tables
        _db.Initialize();

        // Start event polling
        _eventPollTimer = new Timer(
            PollEvents,
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1)
        );
    }

    // Process Management (Ghost Father specific)
    public async Task<ProcessInfo> RegisterProcess(string name, int pid, int port)
    {
        using var conn = _db.CreateConnection();

        var process = new ProcessInfo(
            Guid.NewGuid().ToString(),
            name,
            "running",
            pid,
            port,
            DateTime.UtcNow,
            DateTime.UtcNow
        );

        await conn.ExecuteAsync($@"
            INSERT INTO {_tablePrefix}processes 
                (id, name, status, pid, port, created_at, updated_at)
            VALUES 
                (@Id, @Name, @Status, @Pid, @Port, @CreatedAt, @UpdatedAt)",
            process
        );

        return process;
    }

    public async Task UpdateProcessStatus(string id, string status)
    {
        using var conn = _db.CreateConnection();

        await conn.ExecuteAsync($@"
            UPDATE {_tablePrefix}processes 
            SET status = @status, updated_at = @now
            WHERE id = @id",
            new { id, status, now = DateTime.UtcNow }
        );
    }

    // Configuration Management (shared between Father and children)
    public async Task<T> GetConfig<T>(string key)
    {
        using var conn = _db.CreateConnection();

        var entry = await conn.QueryFirstOrDefaultAsync<ConfigEntry>($@"
            SELECT * FROM {_tablePrefix}config WHERE key = @key",
            new { key }
        );

        return entry != null
            ? System.Text.Json.JsonSerializer.Deserialize<T>(entry.Value)
            : default;
    }

    public async Task SetConfig<T>(string key, T value)
    {
        using var conn = _db.CreateConnection();
        var json = System.Text.Json.JsonSerializer.Serialize(value);

        await conn.ExecuteAsync($@"
            INSERT OR REPLACE INTO {_tablePrefix}config 
                (key, value, app_id, updated_at)
            VALUES 
                (@key, @value, @appId, @now)",
            new { key, value = json, appId = _appId, now = DateTime.UtcNow }
        );

        // Publish config change event
        await PublishEvent("config-changed", new { key, value = json });
    }

    public string GetTablePrefix()
    {
        return "ghost_";
    }
    public DbConnection CreateConnection()
    {
        return _db.CreateConnection();
    }
    public async ValueTask DisposeAsync()
    {
        await _eventPollTimer.DisposeAsync();
    }

    // Event System for Inter-Process Communication
    public async Task PublishEvent(string type, object payload)
    {
        using var conn = _db.CreateConnection();
        var json = System.Text.Json.JsonSerializer.Serialize(payload);

        await conn.ExecuteAsync($@"
            INSERT INTO {_tablePrefix}events 
                (type, app_id, payload, processed)
            VALUES 
                (@type, @appId, @payload, 0)",
            new { type, appId = _appId, payload = json }
        );
    }

    public void SubscribeToEvent<T>(string eventType, Func<T, Task> handler)
    {
        _eventHandlers[eventType] = async (evt) =>
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<T>(evt.Payload);
            await handler(payload);
        };
    }

    private async void PollEvents(object state)
    {
        try
        {
            using var conn = _db.CreateConnection();

            // Get unprocessed events
            var events = await conn.QueryAsync<EventEntry>($@"
                SELECT * FROM {_tablePrefix}events 
                WHERE processed = 0
                ORDER BY created_at ASC
                LIMIT 100"
            );

            foreach (var evt in events)
            {
                if (_eventHandlers.TryGetValue(evt.Type, out var handler))
                {
                    try
                    {
                        await handler(evt);

                        // Mark as processed
                        await conn.ExecuteAsync($@"
                            UPDATE {_tablePrefix}events 
                            SET processed = 1
                            WHERE id = @id",
                            new { evt.Id }
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(_appId, $"Error processing event {evt.Id}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log(_appId, $"Error polling events: {ex.Message}");
        }
    }
}

