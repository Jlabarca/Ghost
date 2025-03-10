using Ghost.Core.Data;
using Ghost.Core.Logging;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Ghost.SDK;

/// <summary>
/// Core connection to GhostFather for management and monitoring
/// </summary>
public class GhostFatherConnection : IAsyncDisposable
{
    private readonly IGhostBus _bus;
    private readonly IGhostLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _healthReportTimer;
    private readonly Timer _metricsReportTimer;
    private bool _isDisposed;

    /// <summary>
    /// Unique identifier for this application instance
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Application metadata
    /// </summary>
    public ProcessMetadata Metadata { get; }

    /// <summary>
    /// Creates a new connection to GhostFather for the specified application
    /// </summary>
    /// <param name="bus">The message bus implementation</param>
    /// <param name="logger">The logger implementation</param>
    /// <param name="metadata">Application metadata</param>
    public GhostFatherConnection(IGhostBus bus, IGhostLogger logger, ProcessMetadata metadata)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

        // Generate unique ID for this instance
        Id = Guid.NewGuid().ToString();

        // Create timers for reporting
        _healthReportTimer = new Timer(
            _ => ReportHealthAsync().ConfigureAwait(false),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30)
        );

        _metricsReportTimer = new Timer(
            _ => ReportMetricsAsync().ConfigureAwait(false),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10)
        );

        // Register with GhostFather
        _ = RegisterWithGhostFatherAsync();

        // Start listening for commands
        _ = StartCommandListenerAsync();

        _logger.LogWithSource($"Connected to GhostFather with ID: {Id}", LogLevel.Information);
    }

    /// <summary>
    /// Event raised when a command is received from GhostFather
    /// </summary>
    public event Func<GhostFatherCommand, Task>? CommandReceived;

    /// <summary>
    /// Reports custom health information to GhostFather
    /// </summary>
    /// <param name="status">Health status (e.g., "Healthy", "Degraded", "Unhealthy")</param>
    /// <param name="details">Optional details about the health status</param>
    /// <param name="customMetrics">Optional custom metrics to include</param>
    public async Task ReportHealthAsync(
        string status = "Healthy",
        string details = "",
        Dictionary<string, object>? customMetrics = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

        try
        {
            var healthInfo = new
            {
                Id,
                AppName = Metadata.Name,
                Status = status,
                Details = details,
                Metrics = customMetrics ?? new Dictionary<string, object>(),
                SystemMetrics = GetSystemMetrics(),
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync($"ghost:health:{Id}", healthInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWithSource($"Failed to report health: {ex.Message}", LogLevel.Error, ex);
        }
    }

    /// <summary>
    /// Reports metrics to GhostFather
    /// </summary>
    /// <param name="metrics">Dictionary of metric names and values</param>
    public async Task ReportMetricsAsync(Dictionary<string, object>? customMetrics = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

        try
        {
            var metrics = new Dictionary<string, object>(GetSystemMetrics());

            // Add custom metrics if provided
            if (customMetrics != null)
            {
                foreach (var (key, value) in customMetrics)
                {
                    metrics[$"custom.{key}"] = value;
                }
            }

            var metricsInfo = new
            {
                Id,
                AppName = Metadata.Name,
                Metrics = metrics,
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync($"ghost:metrics:{Id}", metricsInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWithSource($"Failed to report metrics: {ex.Message}", LogLevel.Error, ex);
        }
    }

    /// <summary>
    /// Sends a log message to GhostFather
    /// </summary>
    /// <param name="message">The log message</param>
    /// <param name="level">The log level</param>
    /// <param name="exception">Optional exception information</param>
    public async Task SendLogAsync(
        string message,
        LogLevel level = LogLevel.Information,
        Exception? exception = null)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

        try
        {
            var logInfo = new
            {
                Id,
                AppName = Metadata.Name,
                Message = message,
                Level = level.ToString(),
                Exception = exception?.ToString(),
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync("ghost:logs", logInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWithSource($"Failed to send log: {ex.Message}", LogLevel.Error, ex);
        }
    }

    /// <summary>
    /// Sends a custom event to GhostFather
    /// </summary>
    /// <param name="eventType">The type of event</param>
    /// <param name="data">The event data</param>
    public async Task SendEventAsync(string eventType, object data)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(GhostFatherConnection));

        try
        {
            var eventInfo = new
            {
                Id,
                AppName = Metadata.Name,
                Type = eventType,
                Data = data,
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync("ghost:events", eventInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWithSource($"Failed to send event: {ex.Message}", LogLevel.Error, ex);
        }
    }

    /// <summary>
    /// Notifies GhostFather that this application is shutting down
    /// </summary>
    /// <param name="reason">Reason for shutdown</param>
    public async Task NotifyShutdownAsync(string reason = "Normal shutdown")
    {
        if (_isDisposed) return;

        try
        {
            var shutdownInfo = new
            {
                Id,
                AppName = Metadata.Name,
                Reason = reason,
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync("ghost:shutdown", shutdownInfo);
            _logger.LogWithSource($"Notified GhostFather of shutdown: {reason}", LogLevel.Information);
        }
        catch (Exception ex)
        {
            _logger.LogWithSource($"Failed to notify shutdown: {ex.Message}", LogLevel.Error, ex);
        }
    }

    private async Task RegisterWithGhostFatherAsync()
    {
        try
        {
            var registrationInfo = new
            {
                Id,
                Metadata.Name,
                Metadata.Type,
                Metadata.Version,
                ProcessId = Environment.ProcessId,
                ExecutablePath = Environment.ProcessPath,
                WorkingDirectory = Environment.CurrentDirectory,
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync("ghost:registration", registrationInfo);
            _logger.LogWithSource($"Registered with GhostFather: {Id}", LogLevel.Information);
        }
        catch (Exception ex)
        {
            _logger.LogWithSource($"Failed to register with GhostFather: {ex.Message}", LogLevel.Error, ex);
        }
    }

    private async Task StartCommandListenerAsync()
    {
        try
        {
            await foreach (var commandJson in _bus.SubscribeAsync<string>($"ghost:command:{Id}", _cts.Token))
            {
                if (string.IsNullOrWhiteSpace(commandJson)) continue;

                try
                {
                    var command = JsonSerializer.Deserialize<GhostFatherCommand>(commandJson);
                    if (command == null) continue;

                    _logger.LogWithSource($"Received command: {command.CommandType}", LogLevel.Debug);

                    // Handle built-in commands
                    switch (command.CommandType.ToLowerInvariant())
                    {
                        case "ping":
                            await SendCommandResponseAsync(command.CommandId, true, "Pong");
                            break;

                        case "stop":
                            await SendCommandResponseAsync(command.CommandId, true, "Stopping");
                            // Raise event for app to handle shutdown
                            await OnCommandReceivedAsync(command);
                            break;

                        default:
                            // Forward to event handler
                            await OnCommandReceivedAsync(command);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWithSource($"Error processing command: {ex.Message}", LogLevel.Error, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation, ignore
        }
        catch (Exception ex)
        {
            _logger.LogWithSource($"Command listener failed: {ex.Message}", LogLevel.Error, ex);
        }
    }

    private async Task OnCommandReceivedAsync(GhostFatherCommand command)
    {
        if (CommandReceived != null)
        {
            try
            {
                await CommandReceived(command);
            }
            catch (Exception ex)
            {
                _logger.LogWithSource($"Error in command handler: {ex.Message}", LogLevel.Error, ex);
                await SendCommandResponseAsync(command.CommandId, false, $"Error: {ex.Message}");
            }
        }
        else
        {
            await SendCommandResponseAsync(command.CommandId, false, "No command handler registered");
        }
    }

    private async Task SendCommandResponseAsync(string commandId, bool success, string message)
    {
        try
        {
            var response = new
            {
                CommandId = commandId,
                Success = success,
                Message = message,
                Timestamp = DateTime.UtcNow
            };

            await _bus.PublishAsync($"ghost:response:{commandId}", response);
        }
        catch (Exception ex)
        {
            _logger.LogWithSource($"Failed to send command response: {ex.Message}", LogLevel.Error, ex);
        }
    }

    private Dictionary<string, object> GetSystemMetrics()
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();

        return new Dictionary<string, object>
        {
            ["memory.workingSet"] = process.WorkingSet64,
            ["memory.privateBytes"] = process.PrivateMemorySize64,
            ["memory.virtualBytes"] = process.VirtualMemorySize64,
            ["cpu.processTime"] = process.TotalProcessorTime.TotalMilliseconds,
            ["process.threads"] = process.Threads.Count,
            ["process.handles"] = process.HandleCount,
            ["gc.totalMemory"] = GC.GetTotalMemory(false),
            ["gc.collections.gen0"] = GC.CollectionCount(0),
            ["gc.collections.gen1"] = GC.CollectionCount(1),
            ["gc.collections.gen2"] = GC.CollectionCount(2)
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        _isDisposed = true;

        // Cancel token to stop subscriptions
        _cts.Cancel();

        // Stop timers
        await _healthReportTimer.DisposeAsync();
        await _metricsReportTimer.DisposeAsync();

        // Notify GhostFather
        await NotifyShutdownAsync("Application disposed");

        // Clean up
        _cts.Dispose();
    }
}

/// <summary>
/// Represents a command from GhostFather
/// </summary>
public class GhostFatherCommand
{
    /// <summary>
    /// Unique command identifier
    /// </summary>
    public string CommandId { get; set; } = "";

    /// <summary>
    /// Type of command (e.g., "stop", "restart", "ping")
    /// </summary>
    public string CommandType { get; set; } = "";

    /// <summary>
    /// Command parameters
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new();
}