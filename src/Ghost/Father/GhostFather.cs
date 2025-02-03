// Ghost/Father/GhostFather.cs
using System.Collections.Concurrent;
using Ghost.Core.Services;
using Ghost.Core.Storage;
using Ghost.SDK;

namespace Ghost.Father;

/// <summary>
/// Core process manager and monitoring service for Ghost applications
/// </summary>
public class GhostFather : GhostApp
{
    private readonly ConcurrentDictionary<string, ProcessInfo> _processes;
    private readonly HealthMonitor _healthMonitor;
    private readonly CommandProcessor _commandProcessor;
    private readonly StateManager _stateManager;

    public GhostFather(GhostOptions options = null) : base(options)
    {
        _processes = new ConcurrentDictionary<string, ProcessInfo>();
        _healthMonitor = new HealthMonitor(Bus, Logger);
        _commandProcessor = new CommandProcessor(Bus, Logger);
        _stateManager = new StateManager(Data, Logger);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Logger.LogInformation("GhostFather starting...");

        try
        {
            // Initialize components
            await InitializeAsync();

            // Start monitoring
            _ = _healthMonitor.StartMonitoringAsync(ct);

            // Start command processing
            _ = _commandProcessor.StartProcessingAsync(ct);

            // Subscribe to system events
            await foreach (var evt in Bus.SubscribeAsync<SystemEvent>("ghost:events", ct))
            {
                await HandleSystemEventAsync(evt);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Fatal error in GhostFather");
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        // Load existing processes
        var processes = await Data.QueryAsync<ProcessInfo>(
            "SELECT * FROM processes WHERE status != @status",
            new { status = ProcessStatus.Stopped });

        foreach (var process in processes)
        {
            _processes[process.Id] = process;
            await _healthMonitor.RegisterProcessAsync(process);
        }

        // Set up command handlers
        _commandProcessor.RegisterHandler("start", HandleStartCommandAsync);
        _commandProcessor.RegisterHandler("stop", HandleStopCommandAsync);
        _commandProcessor.RegisterHandler("restart", HandleRestartCommandAsync);
        _commandProcessor.RegisterHandler("status", HandleStatusCommandAsync);
    }

    private async Task HandleSystemEventAsync(SystemEvent evt)
    {
        try
        {
            switch (evt.Type)
            {
                case "process.registered":
                    await HandleProcessRegistrationAsync(evt);
                    break;

                case "process.stopped":
                    await HandleProcessStoppedAsync(evt);
                    break;

                case "process.crashed":
                    await HandleProcessCrashAsync(evt);
                    break;

                case "health.check":
                    await HandleHealthCheckAsync(evt);
                    break;

                default:
                    Logger.LogWarning("Unknown system event type: {Type}", evt.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error handling system event: {Type}", evt.Type);
        }
    }

    private async Task HandleStartCommandAsync(SystemCommand cmd)
    {
        var processId = cmd.Parameters.GetValueOrDefault("processId");
        if (string.IsNullOrEmpty(processId))
        {
            throw new ArgumentException("Process ID required");
        }

        if (!_processes.TryGetValue(processId, out var process))
        {
            throw new GhostException($"Process not found: {processId}");
        }

        await StartProcessAsync(process);
    }

    private async Task HandleStopCommandAsync(SystemCommand cmd)
    {
        var processId = cmd.Parameters.GetValueOrDefault("processId");
        if (string.IsNullOrEmpty(processId))
        {
            throw new ArgumentException("Process ID required");
        }

        if (!_processes.TryGetValue(processId, out var process))
        {
            throw new GhostException($"Process not found: {processId}");
        }

        await StopProcessAsync(process);
    }

    private async Task HandleRestartCommandAsync(SystemCommand cmd)
    {
        var processId = cmd.Parameters.GetValueOrDefault("processId");
        if (string.IsNullOrEmpty(processId))
        {
            throw new ArgumentException("Process ID required");
        }

        if (!_processes.TryGetValue(processId, out var process))
        {
            throw new GhostException($"Process not found: {processId}");
        }

        await RestartProcessAsync(process);
    }

    private async Task HandleStatusCommandAsync(SystemCommand cmd)
    {
        var processId = cmd.Parameters.GetValueOrDefault("processId");

        // Get status for specific process or all processes
        var statuses = string.IsNullOrEmpty(processId)
            ? _processes.Values.ToDictionary(p => p.Id, p => p.Status)
            : _processes.TryGetValue(processId, out var process)
                ? new Dictionary<string, ProcessStatus> { { process.Id, process.Status } }
                : new Dictionary<string, ProcessStatus>();

        await Bus.PublishAsync("ghost:responses", new
        {
            RequestId = cmd.Parameters.GetValueOrDefault("requestId"),
            Statuses = statuses
        });
    }

    private async Task HandleProcessRegistrationAsync(SystemEvent evt)
    {
        try
        {
            var registration = JsonSerializer.Deserialize<ProcessRegistration>(evt.Data);

            var process = new ProcessInfo(
                Id: evt.Source,
                Metadata: new ProcessMetadata(
                    registration.Name,
                    registration.Type,
                    registration.Version,
                    registration.Environment ?? new Dictionary<string, string>(),
                    registration.Configuration ?? new Dictionary<string, string>()
                ),
                StartInfo: CreateStartInfo(registration)
            );

            // Store process
            _processes[process.Id] = process;
            await _stateManager.SaveProcessAsync(process);

            // Start monitoring
            await _healthMonitor.RegisterProcessAsync(process);

            Logger.LogInformation(
                "Registered new process: {Id} ({Name})",
                process.Id,
                registration.Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to handle process registration from {Source}", evt.Source);
        }
    }

    private async Task HandleProcessStoppedAsync(SystemEvent evt)
    {
        if (_processes.TryGetValue(evt.Source, out var process))
        {
            process.Status = ProcessStatus.Stopped;
            await _stateManager.UpdateProcessStatusAsync(evt.Source, ProcessStatus.Stopped);
            Logger.LogInformation("Process stopped: {Id}", evt.Source);
        }
    }

    private async Task HandleProcessCrashAsync(SystemEvent evt)
    {
        if (_processes.TryGetValue(evt.Source, out var process))
        {
            process.Status = ProcessStatus.Crashed;
            await _stateManager.UpdateProcessStatusAsync(evt.Source, ProcessStatus.Crashed);
            Logger.LogError("Process crashed: {Id}", evt.Source);

            // Attempt restart if configured
            if (process.Metadata.Configuration.GetValueOrDefault("autoRestart") == "true")
            {
                await RestartProcessAsync(process);
            }
        }
    }

    private async Task HandleHealthCheckAsync(SystemEvent evt)
    {
        if (_processes.TryGetValue(evt.Source, out var process))
        {
            var health = JsonSerializer.Deserialize<ProcessHealth>(evt.Data);

            // Update process metrics
            await _stateManager.UpdateProcessMetricsAsync(evt.Source, health.Metrics);

            // Check resource thresholds
            if (health.Metrics.CpuPercentage > 90 || health.Metrics.MemoryBytes > 1_000_000_000)
            {
                process.Status = ProcessStatus.Warning;
                await _stateManager.UpdateProcessStatusAsync(evt.Source, ProcessStatus.Warning);

                Logger.LogWarning(
                    "Process {Id} resource usage high - CPU: {Cpu}%, Memory: {Memory}MB",
                    evt.Source,
                    health.Metrics.CpuPercentage,
                    health.Metrics.MemoryBytes / 1024 / 1024);
            }
        }
    }

    private async Task StartProcessAsync(ProcessInfo process)
    {
        try
        {
            Logger.LogInformation("Starting process: {Id}", process.Id);

            await process.StartAsync();
            process.Status = ProcessStatus.Running;

            await _stateManager.UpdateProcessStatusAsync(process.Id, ProcessStatus.Running);
        }
        catch (Exception ex)
        {
            process.Status = ProcessStatus.Failed;
            await _stateManager.UpdateProcessStatusAsync(process.Id, ProcessStatus.Failed);

            Logger.LogError(ex, "Failed to start process: {Id}", process.Id);
            throw;
        }
    }

    private async Task StopProcessAsync(ProcessInfo process)
    {
        try
        {
            Logger.LogInformation("Stopping process: {Id}", process.Id);

            await process.StopAsync(TimeSpan.FromSeconds(30));
            process.Status = ProcessStatus.Stopped;

            await _stateManager.UpdateProcessStatusAsync(process.Id, ProcessStatus.Stopped);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to stop process: {Id}", process.Id);
            throw;
        }
    }

    private async Task RestartProcessAsync(ProcessInfo process)
    {
        try
        {
            Logger.LogInformation("Restarting process: {Id}", process.Id);

            await StopProcessAsync(process);
            await StartProcessAsync(process);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to restart process: {Id}", process.Id);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRegistration registration)
    {
        return new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{registration.Path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["GHOST_PROCESS_ID"] = registration.Id,
                ["GHOST_ENVIRONMENT"] = registration.Environment?
                    .GetValueOrDefault("ASPNETCORE_ENVIRONMENT", "Production")
            }
        };
    }

    public override async Task StopAsync()
    {
        // Stop all processes
        var stopTasks = _processes.Values
            .Where(p => p.Status == ProcessStatus.Running)
            .Select(p => StopProcessAsync(p));

        await Task.WhenAll(stopTasks);

        await base.StopAsync();
    }
}

