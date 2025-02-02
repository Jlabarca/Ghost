using Ghost.Infrastructure.Monitoring;
using System.Collections.Concurrent;

namespace Ghost.Infrastructure.Storage;

/// <summary>
/// Implements a circular buffer for storing time-series metrics data
/// with automatic pruning of old data.
/// </summary>
public class MetricsCircularBuffer : IAsyncDisposable
{
    private readonly IRedisClient _redis;
    private readonly string _bufferKey;
    private readonly TimeSpan _retentionPeriod;
    private readonly int _maxBufferSize;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _lastPruneTime;
    private bool _disposed;

    public MetricsCircularBuffer(
        IRedisClient redis,
        string bufferKey,
        TimeSpan retentionPeriod,
        int maxBufferSize = 10000)
    {
        _redis = redis;
        _bufferKey = bufferKey;
        _retentionPeriod = retentionPeriod;
        _maxBufferSize = maxBufferSize;
        _lastPruneTime = new ConcurrentDictionary<string, DateTime>();
    }

    public async Task AddMetricsAsync(ProcessMetrics metrics)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetricsCircularBuffer));

        await _lock.WaitAsync();
        try
        {
            // Create buffer key specific to process
            var processBufferKey = $"{_bufferKey}:{metrics.ProcessId}";

            // Add metrics to buffer
            var serialized = System.Text.Json.JsonSerializer.Serialize(metrics);
            await _redis.PublishAsync(processBufferKey, serialized);

            // Check if we need to prune
            var lastPrune = _lastPruneTime.GetOrAdd(metrics.ProcessId, DateTime.UtcNow);
            if (DateTime.UtcNow - lastPrune > TimeSpan.FromMinutes(5))
            {
                await PruneBufferAsync(processBufferKey);
                _lastPruneTime[metrics.ProcessId] = DateTime.UtcNow;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async IAsyncEnumerable<ProcessMetrics> GetMetricsAsync(
        string processId,
        DateTime from,
        DateTime to,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetricsCircularBuffer));

        var processBufferKey = $"{_bufferKey}:{processId}";

        await foreach (var message in _redis.SubscribeAsync(processBufferKey, cancellationToken))
        {
            if (string.IsNullOrEmpty(message)) continue;

            var metrics = System.Text.Json.JsonSerializer
                .Deserialize<ProcessMetrics>(message);

            if (metrics != null && metrics.Timestamp >= from && metrics.Timestamp <= to)
            {
                yield return metrics;
            }
        }
    }

    private async Task PruneBufferAsync(string bufferKey)
    {
        var cutoffTime = DateTime.UtcNow - _retentionPeriod;
        var prunedBuffer = new List<ProcessMetrics>();

        await foreach (var message in _redis.SubscribeAsync(bufferKey))
        {
            if (string.IsNullOrEmpty(message)) continue;

            var metrics = System.Text.Json.JsonSerializer
                .Deserialize<ProcessMetrics>(message);

            if (metrics != null && metrics.Timestamp >= cutoffTime)
            {
                prunedBuffer.Add(metrics);
            }
        }

        // Keep only up to max buffer size, sorted by timestamp
        if (prunedBuffer.Count > _maxBufferSize)
        {
            prunedBuffer = prunedBuffer
                .OrderByDescending(m => m.Timestamp)
                .Take(_maxBufferSize)
                .ToList();
        }

        // Clear and rewrite buffer
        await _redis.DeleteAsync(bufferKey);
        foreach (var metrics in prunedBuffer)
        {
            var serialized = System.Text.Json.JsonSerializer.Serialize(metrics);
            await _redis.PublishAsync(bufferKey, serialized);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _lock.WaitAsync();
        try
        {
            _disposed = true;
            _lastPruneTime.Clear();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}