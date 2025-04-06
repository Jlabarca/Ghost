namespace Ghost.SDK;

/// <summary>
/// Interface for automatic metrics collection
/// </summary>
public interface IAutoMonitor : IAsyncDisposable
{
  /// <summary>
  /// Starts the automatic metrics collection
  /// </summary>
  Task StartAsync(CancellationToken ct = default);

  /// <summary>
  /// Stops the automatic metrics collection
  /// </summary>
  Task StopAsync(CancellationToken ct = default);

  /// <summary>
  /// Tracks a specific event occurrence
  /// </summary>
  /// <param name="eventName">Name of the event</param>
  /// <param name="properties">Optional event properties</param>
  Task TrackEventAsync(string eventName, Dictionary<string, string>? properties = null);

  /// <summary>
  /// Manually track metrics
  /// </summary>
  /// <param name="customMetrics">Custom metrics to track</param>
  Task TrackMetricsAsync(Dictionary<string, double>? customMetrics = null);

  /// <summary>
  /// Sets the collection interval
  /// </summary>
  /// <param name="interval">Interval between metric collections</param>
  void SetCollectionInterval(TimeSpan interval);

  /// <summary>
  /// Registers a custom metrics provider
  /// </summary>
  /// <param name="provider">Function that returns custom metrics</param>
  void RegisterCustomMetricsProvider(Func<Dictionary<string, double>> provider);
}
