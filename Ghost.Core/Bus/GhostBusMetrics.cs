using System.Collections.Concurrent;
namespace Ghost.Storage.Metrics;

public class GhostBusMetrics : IGhostBusMetrics
{
  private long _totalMessagesPublished;
  private long _totalMessagesReceived;
  private long _totalErrors;
  private readonly ConcurrentDictionary<string, ChannelMetrics> _channelMetrics = new();
  private int _activeSubscriptions;
  private int _activeChannels;
        
  public void IncrementMessagePublished(string channel)
  {
    Interlocked.Increment(ref _totalMessagesPublished);
    _channelMetrics.GetOrAdd(channel, _ => new ChannelMetrics())
        .IncrementPublished();
  }
        
  public void IncrementMessageReceived(string channel)
  {
    Interlocked.Increment(ref _totalMessagesReceived);
    _channelMetrics.GetOrAdd(channel, _ => new ChannelMetrics())
        .IncrementReceived();
  }
        
  public void RecordPublishLatency(string channel, TimeSpan duration)
  {
    _channelMetrics.GetOrAdd(channel, _ => new ChannelMetrics())
        .RecordPublishLatency(duration);
  }
        
  public void RecordSubscriptionLatency(string channel, TimeSpan duration)
  {
    _channelMetrics.GetOrAdd(channel, _ => new ChannelMetrics())
        .RecordSubscriptionLatency(duration);
  }
        
  public void IncrementErrors(string operation, string channel)
  {
    Interlocked.Increment(ref _totalErrors);
    _channelMetrics.GetOrAdd(channel, _ => new ChannelMetrics())
        .IncrementErrors(operation);
  }
        
  public void UpdateActiveSubscriptions(int count)
  {
    Interlocked.Exchange(ref _activeSubscriptions, count);
  }
        
  public void UpdateActiveChannels(int count)
  {
    Interlocked.Exchange(ref _activeChannels, count);
  }
        
  public Dictionary<string, object> GetMetricsSnapshot()
  {
    var snapshot = new Dictionary<string, object>
    {
        ["total_messages_published"] = _totalMessagesPublished,
        ["total_messages_received"] = _totalMessagesReceived,
        ["total_errors"] = _totalErrors,
        ["active_subscriptions"] = _activeSubscriptions,
        ["active_channels"] = _activeChannels,
        ["channel_metrics"] = _channelMetrics.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.GetSnapshot()
        )
    };
    return snapshot;
  }
        
  private class ChannelMetrics
  {
    private long _published;
    private long _received;
    private long _errors;
    private readonly object _latencyLock = new();
    private readonly List<double> _publishLatencies = new();
    private readonly List<double> _subscriptionLatencies = new();
            
    public void IncrementPublished() => Interlocked.Increment(ref _published);
    public void IncrementReceived() => Interlocked.Increment(ref _received);
    public void IncrementErrors(string operation) => Interlocked.Increment(ref _errors);
            
    public void RecordPublishLatency(TimeSpan duration)
    {
      lock (_latencyLock)
      {
        _publishLatencies.Add(duration.TotalMilliseconds);
        // Keep only last 1000 samples
        if (_publishLatencies.Count > 1000)
          _publishLatencies.RemoveAt(0);
      }
    }
            
    public void RecordSubscriptionLatency(TimeSpan duration)
    {
      lock (_latencyLock)
      {
        _subscriptionLatencies.Add(duration.TotalMilliseconds);
        // Keep only last 1000 samples
        if (_subscriptionLatencies.Count > 1000)
          _subscriptionLatencies.RemoveAt(0);
      }
    }
            
    public object GetSnapshot()
    {
      lock (_latencyLock)
      {
        return new
        {
            published = _published,
            received = _received,
            errors = _errors,
            publish_latency_avg = _publishLatencies.Count > 0 ? _publishLatencies.Average() : 0,
            publish_latency_p95 = CalculatePercentile(_publishLatencies, 0.95),
            subscription_latency_avg = _subscriptionLatencies.Count > 0 ? _subscriptionLatencies.Average() : 0,
            subscription_latency_p95 = CalculatePercentile(_subscriptionLatencies, 0.95)
        };
      }
    }
            
    private double CalculatePercentile(List<double> values, double percentile)
    {
      if (values.Count == 0) return 0;
      var sorted = values.OrderBy(v => v).ToList();
      int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
      return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }
  }
}
