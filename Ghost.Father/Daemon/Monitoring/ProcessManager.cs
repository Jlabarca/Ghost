using Ghost.Config;
using Ghost.Data;
using Ghost.Exceptions;
using Ghost.Modules;
using Ghost.Storage;
using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Diagnostics;
using Spectre.Console;

namespace Ghost.Father;

/// <summary>
/// Main process management system that handles process lifecycle and orchestration
/// </summary>
public class ProcessManager : IAsyncDisposable
{
  private readonly IGhostBus _bus;
  private readonly IGhostData _data;
  private readonly GhostConfig _config;
  private readonly ConcurrentDictionary<string, ProcessInfo> _processes;
  private readonly SemaphoreSlim _lock = new(1, 1);
  private readonly HealthMonitor _healthMonitor;
  private readonly StateManager _stateManager;
  private bool _disposed;

  // Configurable settings
  private readonly TimeSpan _shutdownTimeout = TimeSpan.FromSeconds(30);
  private readonly TimeSpan _startupTimeout = TimeSpan.FromSeconds(30);
  private readonly int _maxStartAttempts = 3;


  public ProcessManager(IServiceCollection services)
  {
    var serviceProvider = services.BuildServiceProvider();
    _bus = serviceProvider.GetRequiredService<IGhostBus>();
    _data = serviceProvider.GetRequiredService<IGhostData>();
    _config = serviceProvider.GetRequiredService<GhostConfig>();
    _healthMonitor = serviceProvider.GetRequiredService<HealthMonitor>();
    _stateManager = serviceProvider.GetRequiredService<StateManager>();
    _processes = new ConcurrentDictionary<string, ProcessInfo>();
    InitializeAsync();
  }

  public async Task InitializeAsync()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(ProcessManager));
    await _lock.WaitAsync();
    try
    {
      // Initialize schema
      //await _stateManager.InitializeAsync();//InitializeSchemaAsync();

      // Initialize state manager
      await _stateManager.InitializeAsync();

      // Load persisted processes
      var states = await _stateManager.GetActiveProcessesAsync();
      foreach (var state in states)
      {
        _processes[state.Id] = state;
        await _healthMonitor.RegisterProcessAsync(state);
        G.LogInfo($"Loaded process state: {state.Id} ({state.Status})");
      }

      // Start health monitoring
      await _healthMonitor.StartMonitoringAsync(CancellationToken.None);

      // Subscribe to system events
      _ = SubscribeToSystemEventsAsync();

      G.LogInfo($"Process manager initialized with {_processes.Count} processes");
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to initialize process manager");
      throw;
    }
    finally
    {
      _lock.Release();
    }
  }
  private async Task InitializeSchemaAsync()
  {
    try
    {
      // Check if schema version table exists
      var schemaVersionTableExists = await _data.TableExistsAsync("ghost_schema_version");

      if (!schemaVersionTableExists)
      {
        // Create initial schema
        await CreateInitialSchemaAsync();
        return;
      }

      // Check current schema version
      var currentVersion = await _data.QuerySingleAsync<int>(@"
            SELECT COALESCE(MAX(version), 0) FROM ghost_schema_version");

      // Apply migrations sequentially
      if (currentVersion < 2)
      {
        await MigrateToVersion2Async();
      }

      if (currentVersion < 3)
      {
        await MigrateToVersion3Async();
      }

      G.LogInfo($"PostgreSQL schema is up to date (version {Math.Max(currentVersion, 3)})");
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to initialize PostgreSQL schema");
      throw new GhostException("Failed to initialize database schema", ex, ErrorCode.StorageConfigurationFailed);
    }
  }

  private async Task CreateInitialSchemaAsync()
  {
    // Begin transaction for atomic schema creation
    await using var transaction = await _data.BeginTransactionAsync();
    try
    {
      // Create schema version table
      await transaction.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS ghost_schema_version (
                version INTEGER NOT NULL,
                applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                description TEXT,
                PRIMARY KEY (version)
            )");

      // Create processes table
      await transaction.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS processes (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                type TEXT NOT NULL,
                version TEXT NOT NULL,
                executable_path TEXT NOT NULL,
                arguments TEXT,
                working_directory TEXT,
                status TEXT NOT NULL,
                start_time TIMESTAMP,
                stop_time TIMESTAMP,
                restart_count INTEGER NOT NULL DEFAULT 0,
                last_error TEXT,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                last_seen TIMESTAMP
            )");

      // Create process metadata table
      await transaction.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS process_metadata (
                id SERIAL PRIMARY KEY,
                process_id TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT,
                metadata_type TEXT NOT NULL,
                UNIQUE(process_id, key, metadata_type),
                FOREIGN KEY (process_id) REFERENCES processes (id) ON DELETE CASCADE
            )");

      // Create process metrics table
      await transaction.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS process_metrics (
                id SERIAL PRIMARY KEY,
                process_id TEXT NOT NULL,
                timestamp TIMESTAMP NOT NULL,
                cpu_percentage REAL,
                memory_bytes BIGINT,
                thread_count INTEGER,
                network_in_bytes BIGINT,
                network_out_bytes BIGINT,
                disk_read_bytes BIGINT,
                disk_write_bytes BIGINT,
                handle_count INTEGER,
                gc_total_memory BIGINT,
                gen0_collections BIGINT,
                gen1_collections BIGINT,
                gen2_collections BIGINT,
                FOREIGN KEY (process_id) REFERENCES processes (id) ON DELETE CASCADE
            )");

      // Create indexes for better performance
      await transaction.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_processes_status ON processes(status)");
      await transaction.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_process_metadata_process_id ON process_metadata(process_id)");
      await transaction.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_process_metrics_process_id_timestamp ON process_metrics(process_id, timestamp)");

      // Record schema version
      await transaction.ExecuteAsync(@"
            INSERT INTO ghost_schema_version (version, description)
            VALUES (1, 'Initial schema creation')");

      await transaction.CommitAsync();
      G.LogInfo("PostgreSQL schema created (version 1)");
    }
    catch
    {
      await transaction.RollbackAsync();
      throw;
    }
  }

  private async Task MigrateToVersion2Async()
  {
    await using var transaction = await _data.BeginTransactionAsync();
    try
    {
      // Add process_health table
      await transaction.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS process_health (
                id SERIAL PRIMARY KEY,
                process_id TEXT NOT NULL,
                status TEXT NOT NULL,
                message TEXT,
                timestamp TIMESTAMP NOT NULL,
                FOREIGN KEY (process_id) REFERENCES processes (id) ON DELETE CASCADE
            )");

      await transaction.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_process_health_process_id_timestamp ON process_health(process_id, timestamp)");

      // Record schema version
      await transaction.ExecuteAsync(@"
            INSERT INTO ghost_schema_version (version, description)
            VALUES (2, 'Added process_health table')");

      await transaction.CommitAsync();
      G.LogInfo("Schema migrated to version 2");
    }
    catch
    {
      await transaction.RollbackAsync();
      throw;
    }
  }

  private async Task MigrateToVersion3Async()
  {
    await using var transaction = await _data.BeginTransactionAsync();
    try
    {
      // Add process_state_log table
      await transaction.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS process_state_log (
                id SERIAL PRIMARY KEY,
                process_id TEXT NOT NULL,
                old_status TEXT,
                new_status TEXT NOT NULL,
                timestamp TIMESTAMP NOT NULL,
                FOREIGN KEY (process_id) REFERENCES processes (id) ON DELETE CASCADE
            )");

      await transaction.ExecuteAsync(@"
            CREATE INDEX IF NOT EXISTS idx_process_state_log_process_id_timestamp ON process_state_log(process_id, timestamp)");

      // Add retention policy to process metrics
      // PostgreSQL-specific - create a retention management function
      await transaction.ExecuteAsync(@"
            CREATE OR REPLACE FUNCTION trim_process_metrics() RETURNS void AS $$
            BEGIN
                DELETE FROM process_metrics
                WHERE timestamp < NOW() - INTERVAL '7 days';
            END;
            $$ LANGUAGE plpgsql;");

      // Record schema version
      await transaction.ExecuteAsync(@"
            INSERT INTO ghost_schema_version (version, description)
            VALUES (3, 'Added process_state_log table and metrics retention')");

      await transaction.CommitAsync();
      G.LogInfo("Schema migrated to version 3");
    }
    catch
    {
      await transaction.RollbackAsync();
      throw;
    }
  }

  private async Task SubscribeToSystemEventsAsync()
  {
    try
    {
      await foreach (var evt in _bus.SubscribeAsync<SystemEvent>("ghost:events"))
      {
        try
        {
          await HandleSystemEventAsync(evt);
        }
        catch (Exception ex)
        {
          G.LogError(ex, "Error handling system event: {Type}", evt.Type);
        }
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Fatal error in system event subscription");
      throw;
    }
  }

  private async Task HandleSystemEventAsync(SystemEvent evt)
  {
    switch (evt.Type)
    {
      case "process.registered":
        await HandleProcessRegistrationAsync(evt);
        break;
      case "process.stopped":
        await HandleProcessStoppedAsync(evt.ProcessId);
        break;
      case "process.crashed":
        await HandleProcessCrashAsync(evt.ProcessId);
        break;
      default:
        G.LogWarn("Unknown system event type: {Type}", evt.Type);
        break;
    }
  }

  public async Task<ProcessInfo> RegisterProcessAsync(ProcessRegistration registration)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(ProcessManager));
    if (registration == null) throw new ArgumentNullException(nameof(registration));

    await _lock.WaitAsync();
    try
    {
      // Validate registration
      if (string.IsNullOrEmpty(registration.Id))
        throw new ArgumentException("Process ID cannot be empty");
      if (string.IsNullOrEmpty(registration.ExecutablePath))
        throw new ArgumentException("Executable path cannot be empty");

      // Create process metadata
      var metadata = new ProcessMetadata(
          Name: Path.GetFileNameWithoutExtension(registration.ExecutablePath),
          Type: registration.Type ?? "generic",
          Version: registration.Version ?? "1.0.0",
          Environment: registration.Environment ?? new Dictionary<string, string>(),
          Configuration: registration.Configuration ?? new Dictionary<string, string>()
      );

      // Create process
      var process = new ProcessInfo(
          id: registration.Id,
          metadata: metadata,
          executablePath: registration.ExecutablePath,
          arguments: registration.Arguments ?? string.Empty,
          workingDirectory: registration.WorkingDirectory ?? Path.GetDirectoryName(registration.ExecutablePath),
          environment: registration.Environment ?? new Dictionary<string, string>()
      );

      // Store process
      if (!_processes.TryAdd(process.Id, process))
      {
        throw new GhostException(
            $"Process with ID {process.Id} already exists", ErrorCode.ProcessError);
      }

      // Save state and register for monitoring
      await _stateManager.SaveProcessAsync(process);
      await _healthMonitor.RegisterProcessAsync(process);
      G.LogInfo($"Registered new process: {process.Id} ({process.Metadata.Name})");

      return process;
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task StartProcessAsync(string id)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(ProcessManager));
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("Process ID cannot be empty", nameof(id));

    await _lock.WaitAsync();
    try
    {
      if (!_processes.TryGetValue(id, out var process))
      {
        throw new GhostException($"Process not found: {id}", ErrorCode.ProcessError);
      }

      if (process.Status == ProcessStatus.Running)
      {
        G.LogWarn("Process already running: {Id}", id);
        return;
      }

      // Load process configuration
      var config = _config.GetModuleConfig<ProcessConfig>($"processes:{id}");

      // Configure environment from config if provided
      if (config?.Environment != null)
      {
        foreach (var (key, value) in config.Environment)
        {
          process.Metadata.Environment[key] = value;
        }
      }

      // Attempt to start with retry logic
      var attempts = 0;
      var lastError = default(Exception);
      while (attempts++ < _maxStartAttempts)
      {
        try
        {
          await process.StartAsync();
          await _stateManager.SaveProcessAsync(process);
          G.LogInfo("Started process: {Id} (attempt {Attempt}/{Max})", id, attempts, _maxStartAttempts);
          return;
        }
        catch (Exception ex)
        {
          lastError = ex;
          G.LogWarn("Failed to start process: {Id} (attempt {Attempt}/{Max})", id, attempts, _maxStartAttempts);
          if (attempts < _maxStartAttempts)
          {
            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempts)));
          }
        }
      }

      throw new GhostException(
          $"Failed to start process after {_maxStartAttempts} attempts: {id}",
          lastError,
          ErrorCode.ProcessStartFailed);
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task StopProcessAsync(string id)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(ProcessManager));
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("Process ID cannot be empty", nameof(id));

    await _lock.WaitAsync();
    try
    {
      if (!_processes.TryGetValue(id, out var process))
      {
        throw new GhostException($"Process not found: {id}", ErrorCode.ProcessError);
      }

      if (process.Status == ProcessStatus.Stopped)
      {
        G.LogWarn("Process already stopped: {Id}", id);
        return;
      }

      // Attempt graceful shutdown
      try
      {
        await process.StopAsync(_shutdownTimeout);
        await _stateManager.SaveProcessAsync(process);
        G.LogInfo("Stopped process: {Id}", id);
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Error stopping process: {Id}", id);
        throw new GhostException(
            $"Failed to stop process: {id}", ex, ErrorCode.ProcessError);
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task RestartProcessAsync(string id)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(ProcessManager));
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("Process ID cannot be empty", nameof(id));

    await _lock.WaitAsync();
    try
    {
      if (!_processes.TryGetValue(id, out var process))
      {
        throw new GhostException($"Process not found: {id}", ErrorCode.ProcessError);
      }

      await process.RestartAsync(_shutdownTimeout);
      await _stateManager.SaveProcessAsync(process);
      G.LogInfo("Restarted process: {Id} (restart count: {Count})", id, process.RestartCount);
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task<ProcessInfo> GetProcessAsync(string id)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(ProcessManager));
    if (string.IsNullOrEmpty(id)) throw new ArgumentException("Process ID cannot be empty", nameof(id));

    if (_processes.TryGetValue(id, out var process))
    {
      return process;
    }

    throw new GhostException($"Process not found: {id}", ErrorCode.ProcessError);
  }

  public IEnumerable<ProcessInfo> GetAllProcesses()
  {
    if (_disposed) throw new ObjectDisposedException(nameof(ProcessManager));
    return _processes.Values.ToList();
  }

  private async Task HandleProcessRegistrationAsync(SystemEvent evt)
  {
    try
    {
      // Use MemoryPack instead of JsonSerializer
      var registration = MemoryPackSerializer.Deserialize<ProcessRegistration>(evt.Data);
      if (registration == null)
      {
        G.LogError("Invalid process registration data");
        return;
      }

      await RegisterProcessAsync(registration);
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Failed to handle process registration");
    }
  }

  private async Task HandleProcessStoppedAsync(string processId)
  {
    if (_processes.TryGetValue(processId, out var process))
    {
      await process.StopAsync(TimeSpan.FromSeconds(5));
      await _stateManager.SaveProcessAsync(process);
      G.LogInfo("Process stopped: {Id}", processId);
    }
  }

  private async Task HandleProcessCrashAsync(string processId)
  {
    if (_processes.TryGetValue(processId, out var process))
    {
      await process.StopAsync(TimeSpan.FromSeconds(5));
      await _stateManager.SaveProcessAsync(process);

      // Check auto-restart configuration
      var config = _config.GetModuleConfig<ProcessConfig>($"processes:{processId}");
      if (config?.AutoRestart == true)
      {
        G.LogInfo("Auto-restarting crashed process: {Id}", processId);
        await Task.Delay(config.RestartDelayMs);
        await RestartProcessAsync(processId);
      } else
      {
        G.LogError("Process crashed: {Id}", processId);
      }
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed) return;
    await _lock.WaitAsync();
    try
    {
      if (_disposed) return;

      // Stop all processes
      var stopTasks = _processes.Values
          .Where(p => p.Status == ProcessStatus.Running)
          .Select(p => StopProcessAsync(p.Id));

      try
      {
        await Task.WhenAll(stopTasks);
      }
      catch (Exception ex)
      {
        G.LogError(ex, "Errors occurred while stopping processes during shutdown");
      }

      _processes.Clear();
      _lock.Dispose();
      _disposed = true;
      G.LogInfo("Process manager disposed");
    }
    finally
    {
      if (!_disposed)
      {
        _lock.Release();
      }
    }
  }

  public async Task<object> GetProcessStatusAsync(string? processId)
  {
    return await _stateManager.GetProcessStatusAsync(processId);
  }

  public async Task StopAllAsync()
  {
    var runningProcesses = _processes.Values
        .Where(p => p.Status == ProcessStatus.Running)
        .ToList();

    foreach (var process in runningProcesses)
    {
      await process.StopAsync(_shutdownTimeout);
      await _stateManager.SaveProcessAsync(process);
    }
  }

  public async Task MaintenanceTickAsync()
  {
    await _lock.WaitAsync();
    try
    {
      var runningProcesses = _processes.Values
          .Where(p => p.Status == ProcessStatus.Running)
          .ToList();

      foreach (var process in runningProcesses)
      {
        // we supposedly already did a bunch of health checks (await _healthMonitor.CheckHealthAsync(process))
        // so here we try to take action based on the health of the process
        switch (process.Status)
        {
          case ProcessStatus.Starting:
          case ProcessStatus.Running:
            continue;
          case ProcessStatus.Stopping:
          case ProcessStatus.Stopped:
            G.LogDebug($"Process is healthy: {process.Id} ({process.Metadata.Name})");
            continue;
          case ProcessStatus.Failed:
          case ProcessStatus.Crashed:
          case ProcessStatus.Warning:
            break;
          default:
            throw new ArgumentOutOfRangeException();
        }

        G.LogWarn(string.Format("Process is unhealthy: {Id} ({Name})", process.Id, process.Metadata.Name));
        // Attempt to restart process
        await process.RestartAsync(_shutdownTimeout);
        await _stateManager.SaveProcessAsync(process);
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Error during maintenance tick");
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task<List<ProcessInfo>> GetAllProcessesAsync()
  {
    await _lock.WaitAsync();
    try
    {
      return _processes.Values.ToList();
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task<object> RunCommandAsync(string command, string args, string workingDirectory, bool waitForExit)
  {
    await _lock.WaitAsync();
    try
    {
      var process = new ProcessInfo(
          id: Guid.NewGuid().ToString(),
          metadata: new ProcessMetadata("Command", "Command", "1.0.0", new Dictionary<string, string>(), new Dictionary<string, string>()),
          executablePath: command,
          arguments: args,
          workingDirectory: workingDirectory,
          environment: new Dictionary<string, string>()
      );

      await process.StartAsync();
      if (waitForExit)
      {
        await process.WaitForExitAsync();
      }

      return process;
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task TerminateGhostProcessesAsync(bool force)
  {
    try
    {
      AnsiConsole.MarkupLine("[grey]Checking for running Ghost processes...[/]");

      var ghostProcesses = FindGhostProcesses();

      if (ghostProcesses.Count > 0)
      {
        AnsiConsole.MarkupLine(
            $"[yellow]Found {ghostProcesses.Count} running Ghost processes that need to be terminated:[/]");

        DisplayProcessList(ghostProcesses);

        if (!force)
        {
          var confirmKill =
              AnsiConsole.Confirm("[yellow]Would you like to terminate these processes?[/]", true);
          if (!confirmKill)
          {
            throw new GhostException(
                "Installation cannot proceed while Ghost processes are running. Please terminate them manually or use --force.",
                ErrorCode.InstallationError);
          }
        }

        await TerminateProcessesAsync(ghostProcesses);

        // Check if all processes were terminated
        var remainingProcesses = FindGhostProcesses();

        if (remainingProcesses.Count > 0 && force)
        {
          AnsiConsole.MarkupLine(
              "[yellow]Warning:[/] Some Ghost processes could not be terminated. Installation may faiG.");
        } else if (remainingProcesses.Count > 0)
        {
          throw new GhostException(
              "Could not terminate all Ghost processes. Please terminate them manually or use --force.",
              ErrorCode.InstallationError);
        } else
        {
          AnsiConsole.MarkupLine("[green]All Ghost processes terminated successfully.[/]");
        }
      } else
      {
        AnsiConsole.MarkupLine("[grey]No running Ghost processes found.[/]");
      }
    }
    catch (Exception ex) when (!(ex is GhostException))
    {
      AnsiConsole.MarkupLine($"[yellow]Warning:[/] Error checking for Ghost processes: {ex.Message}");

      if (!force)
      {
        throw new GhostException(
            "Could not check for running Ghost processes. Please ensure no Ghost processes are running or use --force.",
            ex,
            ErrorCode.InstallationError);
      }
    }
  }

  /// <summary>
  /// Finds all running Ghost processes
  /// </summary>
  private List<Process> FindGhostProcesses()
  {
    return Process.GetProcesses()
        .Where(p =>
        {
          try
          {
            return p.ProcessName.ToLowerInvariant().Contains("ghost") ||
                   (p.MainModule?.FileName?.Contains("\\Ghost\\", StringComparison.OrdinalIgnoreCase) ??
                    false);
          }
          catch
          {
            // Process access might be denied, skip it
            return false;
          }
        })
        .ToList();
  }

  /// <summary>
  /// Displays a list of processes to the console
  /// </summary>
  private void DisplayProcessList(List<Process> processes)
  {
    foreach (var process in processes)
    {
      try
      {
        string processDetails = $"{process.ProcessName} (PID: {process.Id})";
        try
        {
          if (process.MainModule != null)
          {
            processDetails += $" - {process.MainModule.FileName}";
          }
        }
        catch
        {
          // Ignore if we can't get the module info
        }

        AnsiConsole.MarkupLine($" [grey]· {processDetails}[/]");
      }
      catch
      {
        AnsiConsole.MarkupLine($" [grey]· Unknown process (PID: {process.Id})[/]");
      }
    }
  }

  /// <summary>
  /// Terminates a list of processes
  /// </summary>
  private async Task TerminateProcessesAsync(List<Process> processes)
  {
    foreach (var process in processes)
    {
      try
      {
        AnsiConsole.MarkupLine(
            $"[grey]Terminating process: {process.ProcessName} (PID: {process.Id})[/]");
        process.Kill(true); // true = kill entire process tree
        await Task.Delay(500); // Brief delay to ensure process is terminated
      }
      catch (Exception ex)
      {
        AnsiConsole.MarkupLine(
            $"[yellow]Warning:[/] Could not terminate process {process.Id}: {ex.Message}");
      }
    }

    // Give processes time to properly shut down
    AnsiConsole.MarkupLine("[grey]Waiting for processes to terminate...[/]");
    await Task.Delay(2000);
  }

  public async Task RegisterSelfAsync()
  {
    var selfProcess = Process.GetCurrentProcess();
    var registration = new ProcessRegistration
    {
        Id = "ghost-daemon",
        ExecutablePath = selfProcess.MainModule?.FileName ?? string.Empty,
        Arguments = string.Join(" ", Environment.GetCommandLineArgs()),
        WorkingDirectory = Environment.CurrentDirectory,
        Type = "daemon",
        Version = "1.0.0",
        Environment = new Dictionary<string, string>(),
        Configuration = new Dictionary<string, string>()
    };

    await RegisterProcessAsync(registration);
  }

  public async Task DiscoverGhostAppsAsync()
  {
    var ghostApps = new List<ProcessRegistration>();
    var ghostAppPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ghost", "Apps");

    if (Directory.Exists(ghostAppPath))
    {
      foreach (var appDir in Directory.GetDirectories(ghostAppPath))
      {
        var appName = Path.GetFileName(appDir);
        var appExe = Path.Combine(appDir, $"{appName}.exe");
        if (File.Exists(appExe))
        {
          ghostApps.Add(new ProcessRegistration
          {
              Id = appName,
              ExecutablePath = appExe,
              Arguments = string.Empty,
              WorkingDirectory = appDir,
              Type = "app",
              Version = "1.0.0",
              Environment = new Dictionary<string, string>(),
              Configuration = new Dictionary<string, string>()
          });
        }
      }
    }

    foreach (var app in ghostApps)
    {
      await RegisterProcessAsync(app);
    }
  }
}
/// <summary>
/// Process configuration model
/// </summary>
[MemoryPackable]
public partial class ProcessConfig : ModuleConfigBase
{
  public bool AutoRestart { get; set; } = true;
  public int RestartDelayMs { get; set; } = 5000;
  public Dictionary<string, string> Environment { get; set; } = new();
}
