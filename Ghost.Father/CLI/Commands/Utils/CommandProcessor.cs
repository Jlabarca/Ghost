using Ghost.Core.Exceptions;
using Ghost.Core.Storage;

namespace Ghost.Father;

/// <summary>
/// Handles remote command processing using GhostBus
/// </summary>
public class CommandProcessor
{
    private readonly IGhostBus _bus;
    private readonly Dictionary<string, Func<SystemCommand, Task>> _handlers;

    public CommandProcessor(IGhostBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _handlers = new Dictionary<string, Func<SystemCommand, Task>>();
    }

    public void RegisterHandler(string command, Func<SystemCommand, Task> handler)
    {
        _handlers[command.ToLowerInvariant()] = handler;
    }

    public async Task StartProcessingAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var command in _bus.SubscribeAsync<SystemCommand>("ghost:commands", ct))
            {
                try
                {
                    if (command == null) continue;
                    await ProcessCommandAsync(command);
                }
                catch (Exception ex)
                {
                    G.LogError(ex, "Error processing command");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Fatal error in command processor");
            throw;
        }
    }

    internal async Task ProcessCommandAsync(SystemCommand command)
    {
        try
        {
            // Validate command
            if (string.IsNullOrEmpty(command.CommandType))
            {
                throw new ArgumentException("Command type is required");
            }

            // Find handler
            var handlerKey = command.CommandType.ToLowerInvariant();
            if (!_handlers.TryGetValue(handlerKey, out var handler))
            {
                throw new GhostException(
                    $"Unknown command type: {command.CommandType}",
                    ErrorCode.InvalidOperation);
            }

            // Execute handler
            G.LogDebug(
                "Processing command {Type} for {Target}",
                command.CommandType,
                command.TargetProcessId);

            await handler(command);

            // Send success response
            await SendResponseAsync(command, true, null);
        }
        catch (Exception ex)
        {
            G.LogError(
                ex,
                "Failed to process command {Type} for {Target}",
                command.CommandType,
                command.TargetProcessId);

            // Send error response
            await SendResponseAsync(command, false, ex.Message);
        }
    }

    private async Task SendResponseAsync(SystemCommand command, bool success, string error)
    {
        try
        {
            var response = new CommandResponse
            {
                CommandId = command.CommandId,
                Success = success,
                Error = error,
                Timestamp = DateTime.UtcNow
            };

            // Send to specific response channel if provided
            var responseChannel = command.Parameters.GetValueOrDefault("responseChannel", "ghost:responses");
            await _bus.PublishAsync(responseChannel, response);
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to send command response");
        }
    }
}

public class SystemCommand
{
    public string CommandId { get; set; } = "";
    public string CommandType { get; set; } = "";
    public string TargetProcessId { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public class CommandResponse
{
    public string CommandId { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTime Timestamp { get; set; }
    public object? Data { get; set; }
}