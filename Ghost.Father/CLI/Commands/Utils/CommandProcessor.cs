using Ghost.Core;
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
                    L.LogError(ex, "Error processing command");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Fatal error in command processor");
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
            L.LogDebug(
                "Processing command {Type} for {Target}",
                command.CommandType, command.TargetProcessId);

            await handler(command);

            // Send success response
            await SendResponseAsync(command, true, null, null);
        }
        catch (Exception ex)
        {
            L.LogError(
                ex, "Failed to process command {Type} for {Target}",
                command.CommandType, command.TargetProcessId);

            // Send error response
            await SendResponseAsync(command, false, ex.Message, null);
        }
    }

    private async Task SendResponseAsync(SystemCommand command, bool success, string? error, ICommandData? data = null)
    {
        try
        {
            var response = new CommandResponse
            {
                CommandId = command.CommandId,
                Success = success,
                Error = error,
                Timestamp = DateTime.UtcNow,
                Data = data
            };

            // Send to specific response channel if provided
            var responseChannel = command.Parameters.GetValueOrDefault("responseChannel", "ghost:responses");
            await _bus.PublishAsync(responseChannel, response);
        }
        catch (Exception ex)
        {
            L.LogError(ex, "Failed to send command response");
        }
    }

    // Helper method to send process state
    private Task SendProcessStateResponseAsync(SystemCommand command, bool success, string? error, ProcessState? state)
    {
        var data = state != null ? new ProcessStateResponse { State = state } : null;
        return SendResponseAsync(command, success, error, data);
    }

    // Helper method to send process list

    public Task SendProcessListResponseAsync(SystemCommand command, bool success, string? error, List<ProcessState>? processes)
    {
        var data = processes != null ? new ProcessListResponse { Processes = processes } : null;
        return SendResponseAsync(command, success, error, data);
    }

    // Helper method to send string response
    private Task SendStringResponseAsync(SystemCommand command, bool success, string? error, string? value)
    {
        var data = value != null ? new StringResponse { Value = value } : null;
        return SendResponseAsync(command, success, error, data);
    }

    // Helper method to send boolean response
    private Task SendBooleanResponseAsync(SystemCommand command, bool success, string? error, bool value)
    {
        return SendResponseAsync(command, success, error, new BooleanResponse { Value = value });
    }
}

