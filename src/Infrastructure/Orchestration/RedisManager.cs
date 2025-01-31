using Ghost.Infrastructure.Monitoring;
using System.Text.Json;
using Ghost.Infrastructure.Orchestration.Channels;
using Ghost.Infrastructure.Storage;

namespace Ghost.Infrastructure.Orchestration;

public interface IRedisManager : IAsyncDisposable
{
    Task PublishSystemCommandAsync(SystemCommand command);
    Task PublishMetricsAsync(ProcessMetrics metrics);
    Task PublishStateAsync(string processId, ProcessState state);
    IAsyncEnumerable<SystemCommand> SubscribeToCommandsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<ProcessMetrics> SubscribeToMetricsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<ProcessState> SubscribeToStateAsync(string processId, CancellationToken cancellationToken = default);
}

public class RedisManager : IRedisManager
{
    private readonly IRedisClient _redis;
    private readonly string _commandChannel;
    private readonly string _metricsChannel;
    private readonly string _stateChannel;

    public RedisManager(IRedisClient redis, string systemId)
    {
        _redis = redis;
        _commandChannel = $"ghost:{systemId}:commands";
        _metricsChannel = $"ghost:{systemId}:metrics";
        _stateChannel = $"ghost:{systemId}:state";
    }

    public async Task PublishSystemCommandAsync(SystemCommand command)
    {
        var message = JsonSerializer.Serialize(command);
        await _redis.PublishAsync(_commandChannel, message);
    }

    public async Task PublishMetricsAsync(ProcessMetrics metrics)
    {
        var message = JsonSerializer.Serialize(metrics);
        await _redis.PublishAsync(_metricsChannel, message);
    }

    public async Task PublishStateAsync(string processId, ProcessState state)
    {
        var message = JsonSerializer.Serialize(new { processId, state });
        await _redis.PublishAsync(_stateChannel, message);
    }

    public async IAsyncEnumerable<SystemCommand> SubscribeToCommandsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var message in _redis.SubscribeAsync(_commandChannel, cancellationToken))
        {
            if (message != null)
            {
                var command = JsonSerializer.Deserialize<SystemCommand>(message);
                if (command != null)
                {
                    yield return command;
                }
            }
        }
    }

    public async IAsyncEnumerable<ProcessMetrics> SubscribeToMetricsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var message in _redis.SubscribeAsync(_metricsChannel, cancellationToken))
        {
            if (message != null)
            {
                var metrics = JsonSerializer.Deserialize<ProcessMetrics>(message);
                if (metrics != null)
                {
                    yield return metrics;
                }
            }
        }
    }

    public async IAsyncEnumerable<ProcessState> SubscribeToStateAsync(
        string processId,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var message in _redis.SubscribeAsync(_stateChannel, cancellationToken))
        {
            if (message != null)
            {
                var state = JsonSerializer.Deserialize<ProcessState>(message);
                if (state != null && state.ProcessId == processId)
                {
                    yield return state;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _redis.DisposeAsync();
    }
}
