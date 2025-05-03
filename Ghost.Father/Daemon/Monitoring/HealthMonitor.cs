using Ghost.Core;
using Ghost.Core.Storage;
using Ghost.Father.Models;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Ghost.Father;

/// <summary>
/// Monitors process health and resource usage, providing real-time metrics and status updates
/// </summary>
public class HealthMonitor : IAsyncDisposable
{
    private readonly IGhostBus _bus;
    private readonly ConcurrentDictionary<string, ProcessHealthState> _healthStates;
    private readonly ConcurrentDictionary<string, Stopwatch> _cpuWatches;
    private readonly Timer _healthCheckTimer;
    private readonly TimeSpan _checkInterval;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isDisposed;

    // Configurable thresholds
    private readonly double _cpuWarningThreshold;       // Percentage
    private readonly long _memoryWarningThreshold;      // Bytes
    private readonly int _maxRestartAttempts;
    private readonly TimeSpan _restartCooldown;

    public HealthMonitor(
        IGhostBus bus,
        double cpuWarningThreshold = 90.0,
        long memoryWarningThreshold = 1_000_000_000,  // 1GB
        int maxRestartAttempts = 3,
        TimeSpan? checkInterval = null,
        TimeSpan? restartCooldown = null)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _healthStates = new ConcurrentDictionary<string, ProcessHealthState>();
        _cpuWatches = new ConcurrentDictionary<string, Stopwatch>();
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);
        _healthCheckTimer = new Timer(OnHealthCheck);

        _cpuWarningThreshold = cpuWarningThreshold;
        _memoryWarningThreshold = memoryWarningThreshold;
        _maxRestartAttempts = maxRestartAttempts;
        _restartCooldown = restartCooldown ?? TimeSpan.FromMinutes(5);
    }

    public Task StartMonitoringAsync(CancellationToken ct)
    {
        _healthCheckTimer.Change(TimeSpan.Zero, _checkInterval);
        return Task.CompletedTask;
    }

    public async Task RegisterProcessAsync(ProcessInfo process)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(HealthMonitor));

        await _lock.WaitAsync();
        try
        {
            // Create CPU watch for performance monitoring
            _cpuWatches[process.Id] = Stopwatch.StartNew();

            // Initialize health state
            var healthState = new ProcessHealthState
            {
                ProcessInfo = process,
                LastCheck = DateTime.UtcNow,
                LastRestart = null,
                RestartAttempts = 0,
                Metrics = await CollectProcessMetricsAsync(process),
                Status = new Dictionary<string, string>
                {
                    ["status"] = process.Status.ToString(),
                    ["uptime"] = process.Uptime.ToString(),
                    ["restartCount"] = process.RestartCount.ToString()
                }
            };

            _healthStates[process.Id] = healthState;

            // Subscribe to process events
            process.StatusChanged += OnProcessStatusChanged;
            process.OutputReceived += OnProcessOutputReceived;
            process.ErrorReceived += OnProcessErrorReceived;

            L.LogInfo($"Started monitoring process: {process.Id} ({process.Metadata.Name})");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ProcessMetrics> CollectProcessMetricsAsync(ProcessInfo process)
    {
        try
        {
            var stopwatch = _cpuWatches.GetOrAdd(process.Id, _ => Stopwatch.StartNew());
            var cpuTime = Process.GetCurrentProcess().TotalProcessorTime;
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();

            var cpuUsage = elapsedSeconds > 0
                ? (cpuTime.TotalSeconds / (Environment.ProcessorCount * elapsedSeconds)) * 100
                : 0;

            return new ProcessMetrics(
                ProcessId: process.Id,
                CpuPercentage: Math.Round(cpuUsage, 2),
                MemoryBytes: Process.GetCurrentProcess().WorkingSet64,
                ThreadCount: Process.GetCurrentProcess().Threads.Count,
                Timestamp: DateTime.UtcNow,
                HandleCount: Process.GetCurrentProcess().HandleCount,
                GcTotalMemory: GC.GetTotalMemory(false),
                Gen0Collections: GC.CollectionCount(0),
                Gen1Collections: GC.CollectionCount(1),
                Gen2Collections: GC.CollectionCount(2)
            );
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to collect metrics for process: {Id}", process.Id);
            return ProcessMetrics.CreateSnapshot(process.Id);
        }
    }

    private async void OnHealthCheck(object? state)
    {
        if (_isDisposed) return;

        await _lock.WaitAsync();
        try
        {
            foreach (var healthState in _healthStates.Values)
            {
                try
                {
                    await CheckProcessHealthAsync(healthState);
                }
                catch (Exception ex)
                {
                    L.LogError(ex, "Error checking health for process: {Id}",
                        healthState.ProcessInfo.Id);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task CheckProcessHealthAsync(ProcessHealthState state)
    {
        var process = state.ProcessInfo;

        // Skip check if process is not running
        if (!process.IsRunning) return;

        // Collect current metrics
        var metrics = await CollectProcessMetricsAsync(process);
        state.Metrics = metrics;
        state.LastCheck = DateTime.UtcNow;

        // Check resource thresholds
        var warnings = new List<string>();

        if (metrics.CpuPercentage > _cpuWarningThreshold)
        {
            warnings.Add($"CPU usage is {metrics.CpuPercentage:F1}%");
        }

        if (metrics.MemoryBytes > _memoryWarningThreshold)
        {
            warnings.Add($"Memory usage is {metrics.MemoryBytes / 1024 / 1024}MB");
        }

        // Update status and publish metrics
        state.Status["warnings"] = string.Join(", ", warnings);

        await PublishHealthUpdateAsync(state);

        // Handle warnings
        if (warnings.Any())
        {
            L.LogWarn("Process {Id} health warnings: {Warnings}",
                process.Id, string.Join("; ", warnings));

            // Consider restarting if resource usage is extreme
            if (metrics.CpuPercentage > _cpuWarningThreshold * 1.5 ||
                metrics.MemoryBytes > _memoryWarningThreshold * 1.5)
            {
                await ConsiderRestartAsync(state);
            }
        }
    }

    private async Task ConsiderRestartAsync(ProcessHealthState state)
    {
        // Check restart attempts and cooldown
        if (state.RestartAttempts >= _maxRestartAttempts)
        {
            if (!state.LastRestart.HasValue ||
                DateTime.UtcNow - state.LastRestart.Value > _restartCooldown)
            {
                // Reset counter after cooldown
                state.RestartAttempts = 0;
            }
            else
            {
                // Skip restart if too many attempts
                return;
            }
        }

        try
        {
            await state.ProcessInfo.RestartAsync(TimeSpan.FromSeconds(30));
            state.LastRestart = DateTime.UtcNow;
            state.RestartAttempts++;

            L.LogWarn("Restarted process {Id} due to resource usage (attempt {Attempt}/{Max})",
                state.ProcessInfo.Id, state.RestartAttempts, _maxRestartAttempts);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to restart process: {Id}", state.ProcessInfo.Id);
        }
    }

    private void OnProcessStatusChanged(object? sender, ProcessStatusEventArgs e)
    {
        if (sender is ProcessInfo process && _healthStates.TryGetValue(e.ProcessId, out var state))
        {
            state.Status["status"] = e.NewStatus.ToString();
            state.Status["statusChanged"] = e.Timestamp.ToString("o");
        }
    }

    private void OnProcessOutputReceived(object? sender, ProcessOutputEventArgs e)
    {
        // Could analyze output for health indicators if needed
    }

    private void OnProcessErrorReceived(object? sender, ProcessOutputEventArgs e)
    {
        if (sender is ProcessInfo process && _healthStates.TryGetValue(process.Id, out var state))
        {
            state.Status["lastError"] = e.Data;
            state.Status["lastErrorTime"] = e.Timestamp.ToString("o");
        }
    }

    private async Task PublishHealthUpdateAsync(ProcessHealthState state)
    {
        try
        {
            var health = new ProcessHealth
            {
                ProcessId = state.ProcessInfo.Id,
                Metrics = state.Metrics,
                Status = state.Status,
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync($"ghost:health:{state.ProcessInfo.Id}", health);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to publish health update for process: {Id}",
                state.ProcessInfo.Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        await _lock.WaitAsync();
        try
        {
            if (_isDisposed) return;
            _isDisposed = true;

            await _healthCheckTimer.DisposeAsync();

            foreach (var state in _healthStates.Values)
            {
                state.ProcessInfo.StatusChanged -= OnProcessStatusChanged;
                state.ProcessInfo.OutputReceived -= OnProcessOutputReceived;
                state.ProcessInfo.ErrorReceived -= OnProcessErrorReceived;
            }

            _healthStates.Clear();
            _cpuWatches.Clear();
            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CheckHealthAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var healthState in _healthStates.Values)
            {
                try
                {
                    await CheckProcessHealthAsync(healthState);
                }
                catch (Exception ex)
                {
                    L.LogError(ex, "Error checking health for process: {Id}",
                        healthState.ProcessInfo.Id);
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Maintains the health state for a monitored process
/// </summary>
public class ProcessHealthState
{
    public ProcessInfo ProcessInfo { get; init; }
    public DateTime LastCheck { get; set; }
    public DateTime? LastRestart { get; set; }
    public int RestartAttempts { get; set; }
    public ProcessMetrics Metrics { get; set; }
    public Dictionary<string, string> Status { get; set; } = new();
}