using System.Collections.Concurrent;
using System.Text.Json;
namespace Ghost.Father;

/// <summary>
/// Implementation of automatic metrics collection
/// </summary>
public class AutoMonitor : IAutoMonitor
{
  private readonly IGhostBus _bus;
  private readonly Timer _metricsTimer;
  private readonly ConcurrentDictionary<string, MetricValue> _metrics;
  private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
  private volatile bool _isRunning;
  private readonly SemaphoreSlim _lock = new(1, 1);
  private bool _disposed;

  public AutoMonitor(IGhostBus bus)
  {
    _bus = bus ?? throw new ArgumentNullException(nameof(bus));
    _metrics = new ConcurrentDictionary<string, MetricValue>();
    _metricsTimer = new Timer(CollectMetrics);
  }

  public async Task StartAsync()
  {
    await _lock.WaitAsync();
    try
    {
      if (_isRunning || _disposed) return;
      _isRunning = true;
      _metricsTimer.Change(TimeSpan.Zero, _interval);
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task StopAsync()
  {
    await _lock.WaitAsync();
    try
    {
      if (!_isRunning) return;
      _isRunning = false;
      await _metricsTimer.DisposeAsync();
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task TrackMetricAsync(string name, double value, Dictionary<string, string> tags = null)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));
        
    var metric = new MetricValue 
    { 
        Name = name,
        Value = value,
        Tags = tags ?? new Dictionary<string, string>(),
        Timestamp = DateTime.UtcNow
    };

    _metrics.AddOrUpdate(name, metric, (_, existing) => metric);

    // Publish metric immediately
    await PublishMetricAsync(metric);
  }

  public async Task TrackEventAsync(string name, Dictionary<string, string> properties = null)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));

    await _bus.PublishAsync("ghost:events", new SystemEvent
    {
        Type = "metric.event",
        Source = Process.GetCurrentProcess().Id.ToString(),
        Data = JsonSerializer.Serialize(new
        {
            Name = name,
            Properties = properties ?? new Dictionary<string, string>(),
            Timestamp = DateTime.UtcNow
        })
    });
  }

  public async Task<IEnumerable<MetricReading>> GetMetricsAsync(string name, DateTime start, DateTime end)
  {
    if (_disposed) throw new ObjectDisposedException(nameof(AutoMonitor));

    var readings = new List<MetricReading>();
        
    try
    {
      await foreach (var msg in _bus.SubscribeAsync<string>($"ghost:metrics:{name}"))
      {
        var reading = JsonSerializer.Deserialize<MetricReading>(msg);
        if (reading.Timestamp >= start && reading.Timestamp <= end)
        {
          readings.Add(reading);
        }
      }
    }
    catch (OperationCanceledException)
    {
      // Expected when subscription is cancelled
    }

    return readings;
  }

  private async void CollectMetrics(object state)
  {
    if (!_isRunning || _disposed) return;

    try
    {
      var process = Process.GetCurrentProcess();
      var metrics = new Dictionary<string, double>
      {
          ["cpu.usage"] = await GetCpuUsageAsync(),
          ["memory.private"] = process.PrivateMemorySize64,
