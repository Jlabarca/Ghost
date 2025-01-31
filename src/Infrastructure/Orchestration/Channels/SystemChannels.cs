namespace Ghost.Infrastructure.Orchestration.Channels;

public record SystemCommand(
    string CommandType,
    string TargetProcessId,
    Dictionary<string, string> Parameters);

public record ProcessState(
    string ProcessId,
    string Status,
    Dictionary<string, string> Properties);

public class SystemChannels
{
    private readonly IRedisManager _redisManager;
    private readonly Dictionary<string, Func<SystemCommand, Task>> _commandHandlers;
    private readonly CancellationTokenSource _cts;
    private Task? _processingTask;

    public SystemChannels(IRedisManager redisManager)
    {
        _redisManager = redisManager;
        _commandHandlers = new Dictionary<string, Func<SystemCommand, Task>>();
        _cts = new CancellationTokenSource();
    }

    public void RegisterCommandHandler(string commandType, Func<SystemCommand, Task> handler)
    {
        _commandHandlers[commandType] = handler;
    }

    public async Task StartAsync()
    {
        if (_processingTask != null)
            throw new InvalidOperationException("Channels already started");

        _processingTask = ProcessCommandsAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (_processingTask == null)
            return;

        _cts.Cancel();
        await _processingTask;
        _processingTask = null;
    }

    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        await foreach (var command in _redisManager.SubscribeToCommandsAsync(cancellationToken))
        {
            try
            {
                if (_commandHandlers.TryGetValue(command.CommandType, out var handler))
                {
                    await handler(command);
                }
            }
            catch (Exception ex)
            {
                // Log error handling command
                Console.Error.WriteLine($"Error handling command {command}: {ex.Message}");
            }
        }
    }
}
