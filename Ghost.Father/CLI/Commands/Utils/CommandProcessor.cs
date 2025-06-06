using Ghost;
using Ghost;
using Ghost.Storage;
using System.Text.Json;
public class CommandProcessor
{
    private readonly IGhostBus _bus;
    private readonly Dictionary<string, Func<SystemCommand, Task>> _handlers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _isRunning;

    public CommandProcessor(IGhostBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    }

    public void RegisterHandler(string commandType, Func<SystemCommand, Task> handler)
    {
        if (string.IsNullOrEmpty(commandType)) throw new ArgumentNullException(nameof(commandType));
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        _handlers[commandType] = handler;
        G.LogDebug($"Registered command handler for: {commandType}");
    }

    public async Task StartProcessingAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_isRunning) return;
            _isRunning = true;

            G.LogInfo("Starting command processing...");
            G.LogInfo($"Listening on channel: ghost:commands");

            // TEST: Try subscribing to all channels first
            _ = Task.Run(async () =>
            {
                await foreach (var msg in _bus.SubscribeAsync<object>("*", _cts.Token))
                {
                    var topic = _bus.GetLastTopic();
                    if (topic.Contains("commands"))
                    {
                        G.LogInfo($"[TEST] Received message on commands-related channel: {topic}");
                    }
                }
            });

            // Start listening for commands
            _ = Task.Run(() => ProcessCommandsAsync(_cts.Token), _cts.Token);

            G.LogInfo("Command processor started successfully");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            G.LogInfo("Command processor listening for commands on ghost:commands...");

            await foreach (var message in _bus.SubscribeAsync<SystemCommand>("ghost:commands", cancellationToken))
            {
                G.LogInfo($"[CRITICAL] RAW MESSAGE RECEIVED: {message?.GetType().Name}");

                try
                {
                    if (message == null)
                    {
                        G.LogWarn("Received null command message");
                        continue;
                    }

                    // ADD THIS - Log the raw message before any processing
                    G.LogInfo($"[CRITICAL] COMMAND RECEIVED: Type={message.CommandType}, ID={message.CommandId}");

                    // ... rest of your existing code
                }
                catch (Exception ex)
                {
                    G.LogError(ex, $"Error processing command: {message?.CommandType} ({message?.CommandId})");
                }
            }
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Fatal error in command processor");
        }
    }

    public async Task StopAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!_isRunning) return;
            _isRunning = false;

            _cts.Cancel();
            G.LogInfo("Command processor stopped");
        }
        finally
        {
            _lock.Release();
        }
    }
}