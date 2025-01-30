using Ghost.Infrastructure.Database;
using System.Diagnostics;

namespace Ghost.Infrastructure.Monitoring;

public class MetricsCollector
{
    private readonly GhostDatabase _db;
    private readonly string _processId;
    private readonly GhostLogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly Process _process;
    private Task _collectionTask;

    public MetricsCollector(
        GhostDatabase db,
        string processId,
        GhostLogger logger)
    {
        _db = db;
        _processId = processId;
        _logger = logger;
        _cts = new CancellationTokenSource();
        _process = Process.GetCurrentProcess();
    }

    public Task StartAsync()
    {
        _collectionTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    await CollectAndSendMetrics();
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Log(_processId, $"Error collecting metrics: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);
                }
            }
        });

        return Task.CompletedTask;
    }

    private async Task CollectAndSendMetrics()
    {
        var metrics = new ProcessMetrics(
            CpuPercentage: _process.TotalProcessorTime.TotalMilliseconds / 
                (Environment.ProcessorCount * 100),
            MemoryBytes: _process.WorkingSet64,
            ThreadCount: _process.Threads.Count,
            HandleCount: _process.HandleCount,
            CollectedAt: DateTime.UtcNow
        );

        await _db.PublishEvent("heartbeat", new
        {
            ProcessId = _processId,
            Metrics = metrics,
            Timestamp = DateTime.UtcNow
        });
    }

    public async Task StopAsync()
    {
        if (_collectionTask != null)
        {
            _cts.Cancel();
            await _collectionTask;
            _cts.Dispose();
        }
    }
}
