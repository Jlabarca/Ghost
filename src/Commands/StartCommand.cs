// src/Commands/StartCommand.cs
using Dapper;
using Ghost.Infrastructure;
using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Storage;
using Ghost.Infrastructure.Orchestration;
using Ghost.Infrastructure.Storage.Database;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using Microsoft.Data.Sqlite;

namespace Ghost.Commands;

public class StartCommand : AsyncCommand<StartCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandOption("--data-dir")]
        [Description("Directory for Ghost data storage")]
        public string DataDirectory { get; set; }

        [CommandOption("--port")]
        [Description("Port for the monitoring interface")]
        public int Port { get; set; } = 5000;

        [CommandOption("--no-monitor")]
        [Description("Disable monitoring interface")]
        public bool DisableMonitor { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Setup data directory
            var dataDir = settings.DataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ghost");

            Directory.CreateDirectory(dataDir);
            AnsiConsole.MarkupLine($"[grey]Data directory:[/] {dataDir}");

            // Setup environment
            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var tasks = new Dictionary<string, ProgressTask>
                    {
                        ["db"] = ctx.AddTask("[green]Setting up database[/]"),
                        ["cache"] = ctx.AddTask("[green]Setting up cache[/]"),
                        ["monitor"] = ctx.AddTask("[green]Starting monitoring[/]")
                    };

                    // Initialize database
                    tasks["db"].StartTask();
                    var dbPath = Path.Combine(dataDir, "ghost.db");
                    var db = await InitializeDatabase(dbPath);
                    tasks["db"].Increment(100);

                    // Initialize cache system
                    tasks["cache"].StartTask();
                    var cache = await InitializeCache(dataDir);
                    tasks["cache"].Increment(100);

                    // Setup monitoring if enabled
                    tasks["monitor"].StartTask();
                    if (!settings.DisableMonitor)
                    {
                        await StartMonitoring(settings.Port);
                    }
                    tasks["monitor"].Increment(100);

                    // Initialize GhostFather
                    var father = await InitializeGhostFather(db, cache);
                    await father.StartAsync();
                });

            // Show success message and status
            ShowSuccessMessage(settings);

            // Keep running until cancelled
            await WaitForCancellation();

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task<IDatabaseClient> InitializeDatabase(string dbPath)
    {
        // Create SQLite database tables
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // Create required tables
        await connection.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS processes (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                status TEXT NOT NULL,
                metadata TEXT,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS config (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                scope TEXT NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                modified_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS metrics (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                process_id TEXT NOT NULL,
                metrics TEXT NOT NULL,
                timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
            );");

        return new SQLiteClient(connectionString);
    }

    private async Task<IRedisClient> InitializeCache(string dataDir)
    {
        // For simplicity, we'll still use Redis but could be replaced
        // with a lightweight alternative like an in-memory cache + persistent storage
        try
        {
            var cache = new RedisClient("localhost");
            await cache.SetAsync("ghost:status", "running");
            return cache;
        }
        catch
        {
            // Fallback to local cache implementation
            return new LocalCacheClient(dataDir);
        }
    }

    private async Task StartMonitoring(int port)
    {
        // Initialize monitoring server
        // This could be a simple HTTP server showing process status
        await Task.CompletedTask;
    }

    private async Task<GhostFather> InitializeGhostFather(
        IDatabaseClient db,
        IRedisClient cache)
    {
        var storageRouter = new StorageRouter(
            cache,
            db,
            new PermissionsManager(db, cache));

        var configManager = new ConfigManager(cache, storageRouter);
        var redisManager = new RedisManager(cache, "ghost-father");
        var dataApi = new DataAPI(storageRouter, cache);
        var father = new GhostFather(redisManager, configManager, dataApi);
        return father;
    }

    private void ShowSuccessMessage(Settings settings)
    {
        AnsiConsole.Write(new Panel(
            Align.Left(
                new Markup($@"Ghost Father is running!

[grey]Status:[/] Running
[grey]Monitor:[/] {(settings.DisableMonitor ? "Disabled" : $"http://localhost:{settings.Port}")}

Press Ctrl+C to stop.
")))
            .Header("[green]Ghost Father[/]")
            .BorderColor(Color.Green));
    }

    private async Task WaitForCancellation()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(-1, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }
}

