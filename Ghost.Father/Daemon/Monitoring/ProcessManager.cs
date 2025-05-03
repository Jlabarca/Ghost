using Ghost.Core;
using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Exceptions;
using Ghost.Core.Modules;
using Ghost.Core.Storage;
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


    public ProcessManager(ServiceCollection services)
    {
        using var serviceProvider = services.BuildServiceProvider();
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
            //await _data.InitializeSchemaAsync();

            // Initialize state manager
            await _stateManager.InitializeAsync();

            // Load persisted processes
            var states = await _stateManager.GetActiveProcessesAsync();
            foreach (var state in states)
            {
                _processes[state.Id] = state;
                await _healthMonitor.RegisterProcessAsync(state);
                L.LogInfo("Loaded process state: {0} ({1})", state.Id, state.Status);
            }

            // Start health monitoring
            await _healthMonitor.StartMonitoringAsync(CancellationToken.None);

            // Subscribe to system events
            _ = SubscribeToSystemEventsAsync();

            L.LogInfo("Process manager initialized with {0} processes", _processes.Count);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to initialize process manager");
            throw;
        }
        finally
        {
            _lock.Release();
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
                    L.LogError(ex, "Error handling system event: {Type}", evt.Type);
                }
            }
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Fatal error in system event subscription");
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
                L.LogWarn("Unknown system event type: {Type}", evt.Type);
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
            L.LogInfo("Registered new process: {0} ({1})", process.Id, process.Metadata.Name);

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
                L.LogWarn("Process already running: {Id}", id);
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
                    L.LogInfo("Started process: {Id} (attempt {Attempt}/{Max})", id, attempts, _maxStartAttempts);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    L.LogWarn("Failed to start process: {Id} (attempt {Attempt}/{Max})", id, attempts, _maxStartAttempts);
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
                L.LogWarn("Process already stopped: {Id}", id);
                return;
            }

            // Attempt graceful shutdown
            try
            {
                await process.StopAsync(_shutdownTimeout);
                await _stateManager.SaveProcessAsync(process);
                L.LogInfo("Stopped process: {Id}", id);
            }
            catch (Exception ex)
            {
                L.LogError(ex, "Error stopping process: {Id}", id);
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
            L.LogInfo("Restarted process: {Id} (restart count: {Count})", id, process.RestartCount);
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
                L.LogError("Invalid process registration data");
                return;
            }

            await RegisterProcessAsync(registration);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to handle process registration");
        }
    }

    private async Task HandleProcessStoppedAsync(string processId)
    {
        if (_processes.TryGetValue(processId, out var process))
        {
            await process.StopAsync(TimeSpan.FromSeconds(5));
            await _stateManager.SaveProcessAsync(process);
            L.LogInfo("Process stopped: {Id}", processId);
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
                L.LogInfo("Auto-restarting crashed process: {Id}", processId);
                await Task.Delay(config.RestartDelayMs);
                await RestartProcessAsync(processId);
            }
            else
            {
                L.LogError("Process crashed: {Id}", processId);
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
                L.LogError(ex, "Errors occurred while stopping processes during shutdown");
            }

            _processes.Clear();
            _lock.Dispose();
            _disposed = true;
            L.LogInfo("Process manager disposed");
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
                        L.LogDebug("Process is healthy: {Id} ({Name})", process.Id, process.Metadata.Name);
                        continue;
                    case ProcessStatus.Failed:
                    case ProcessStatus.Crashed:
                    case ProcessStatus.Warning:
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                L.LogWarn("Process is unhealthy: {Id} ({Name})", process.Id, process.Metadata.Name);
                // Attempt to restart process
                await process.RestartAsync(_shutdownTimeout);
                await _stateManager.SaveProcessAsync(process);
            }
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Error during maintenance tick");
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
                        "[yellow]Warning:[/] Some Ghost processes could not be terminated. Installation may fail.");
                }
                else if (remainingProcesses.Count > 0)
                {
                    throw new GhostException(
                        "Could not terminate all Ghost processes. Please terminate them manually or use --force.",
                        ErrorCode.InstallationError);
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]All Ghost processes terminated successfully.[/]");
                }
            }
            else
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
public partial class ProcessConfig : ModuleConfig
{
    public bool AutoRestart { get; set; } = true;
    public int RestartDelayMs { get; set; } = 5000;
    public Dictionary<string, string> Environment { get; set; } = new();
}