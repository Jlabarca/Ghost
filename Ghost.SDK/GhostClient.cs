// using Ghost.Core.Logging;
// using Ghost.Core.Monitoring;
// using Ghost.Core.Storage;
// using Microsoft.Extensions.Logging;
//
// namespace Ghost.SDK;
//
// /// <summary>
// /// A lightweight client for connecting existing applications to GhostFather
// /// </summary>
// public class GhostClient : IAsyncDisposable
// {
//     private readonly IGhostLogger _logger;
//     private readonly IGhostBus _bus;
//     private readonly GhostFatherConnection _connection;
//     private readonly Dictionary<string, Func<Task>> _commandHandlers = new();
//
//     /// <summary>
//     /// Gets the unique ID for this client
//     /// </summary>
//     public string Id => _connection.Id;
//
//     /// <summary>
//     /// Gets the application metadata
//     /// </summary>
//     public ProcessMetadata Metadata => _connection.Metadata;
//
//     /// <summary>
//     /// Creates a new Ghost client for an existing application
//     /// </summary>
//     /// <param name="appName">Application name</param>
//     /// <param name="version">Application version</param>
//     /// <param name="appType">Application type</param>
//     public GhostClient(string appName, string version = "1.0.0", string appType = "external")
//     {
//         // Create minimal metadata
//         var metadata = new ProcessMetadata(
//             Name: appName,
//             Type: appType,
//             Version: version,
//             Environment: new Dictionary<string, string>(),
//             Configuration: new Dictionary<string, string>()
//         );
//
//         // Create basic services
//         _bus = new GhostBus(null);
//         _logger = new DefaultGhostLogger(null, new GhostLoggerConfiguration());
//
//         // Create connection to GhostFather
//         _connection = new GhostFatherConnection(_bus, _logger, metadata);
//         _connection.CommandReceived += HandleCommandAsync;
//
//         // Register default commands
//         RegisterCommand("ping", () => Task.CompletedTask);
//         RegisterCommand("stop", async () =>
//         {
//             // This doesn't actually do anything but respond
//             await _connection.SendLogAsync("Stop command received, but client doesn't support stopping");
//         });
//     }
//
//     /// <summary>
//     /// Registers a custom command handler
//     /// </summary>
//     /// <param name="commandType">The command type to handle</param>
//     /// <param name="handler">The handler function</param>
//     public void RegisterCommand(string commandType, Func<Task> handler)
//     {
//         _commandHandlers[commandType.ToLowerInvariant()] = handler ?? throw new ArgumentNullException(nameof(handler));
//     }
//
//     /// <summary>
//     /// Reports custom health information to GhostFather
//     /// </summary>
//     /// <param name="status">Health status (e.g., "Healthy", "Degraded", "Unhealthy")</param>
//     /// <param name="details">Optional details about the health status</param>
//     /// <param name="metrics">Optional custom metrics to include</param>
//     public Task ReportHealthAsync(string status = "Healthy", string details = "", Dictionary<string, object>? metrics = null)
//     {
//         return _connection.ReportHealthAsync(status, details, metrics);
//     }
//
//     /// <summary>
//     /// Reports custom metrics to GhostFather
//     /// </summary>
//     /// <param name="metrics">Dictionary of metric names and values</param>
//     public Task ReportMetricsAsync(Dictionary<string, object>? metrics = null)
//     {
//         return _connection.ReportMetricsAsync(metrics);
//     }
//
//     /// <summary>
//     /// Sends a log message to GhostFather
//     /// </summary>
//     /// <param name="message">The log message</param>
//     /// <param name="level">The log level</param>
//     /// <param name="exception">Optional exception information</param>
//     public Task SendLogAsync(string message, LogLevel level = LogLevel.Information, Exception? exception = null)
//     {
//         return _connection.SendLogAsync(message, level, exception);
//     }
//
//     /// <summary>
//     /// Sends a custom event to GhostFather
//     /// </summary>
//     /// <param name="eventType">The type of event</param>
//     /// <param name="data">The event data</param>
//     public Task SendEventAsync(string eventType, object data)
//     {
//         return _connection.SendEventAsync(eventType, data);
//     }
//
//     /// <summary>
//     /// Notifies GhostFather that the application is shutting down
//     /// </summary>
//     /// <param name="reason">Reason for shutdown</param>
//     public Task NotifyShutdownAsync(string reason = "Application shutting down")
//     {
//         return _connection.NotifyShutdownAsync(reason);
//     }
//
//     private async Task HandleCommandAsync(GhostFatherCommand command)
//     {
//         var commandType = command.CommandType.ToLowerInvariant();
//
//         if (_commandHandlers.TryGetValue(commandType, out var handler))
//         {
//             try
//             {
//                 await handler();
//             }
//             catch (Exception ex)
//             {
//                 _logger.LogWithSource($"Error executing command handler for {commandType}: {ex.Message}",
//                     LogLevel.Error, ex);
//             }
//         }
//         else
//         {
//             _logger.LogWithSource($"No handler registered for command: {commandType}",
//                 LogLevel.Warning);
//         }
//     }
//
//     /// <summary>
//     /// Disposes resources used by the client
//     /// </summary>
//     public async ValueTask DisposeAsync()
//     {
//         await _connection.DisposeAsync();
//     }
// }