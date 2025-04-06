using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Storage;
namespace Ghost.SDK;

/// <summary>
/// Manages access to the messaging bus
/// </summary>
public class BusManager : IAsyncDisposable
{
  private readonly IGhostBus _bus;

  public BusManager(GhostConfig config)
  {
    // Initialize bus based on config
    string cachePath = Path.Combine(
        config.Core.DataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ghost",
            "data"),
        "cache");

    Directory.CreateDirectory(cachePath);
    var cache = new LocalCache(cachePath);

    _bus = new GhostBus(cache);
  }

  /// <summary>
  /// Get the raw bus object
  /// </summary>
  public IGhostBus Bus => _bus;

  /// <summary>
  /// Publish a message to a channel
  /// </summary>
  public async Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null)
  {
    await _bus.PublishAsync(channel, message, expiry);
  }

  /// <summary>
  /// Subscribe to a channel
  /// </summary>
  public IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, CancellationToken cancellationToken = default)
  {
    return _bus.SubscribeAsync<T>(channelPattern, cancellationToken);
  }

  /// <summary>
  /// Unsubscribe from a channel
  /// </summary>
  public async Task UnsubscribeAsync(string channelPattern)
  {
    await _bus.UnsubscribeAsync(channelPattern);
  }

  /// <summary>
  /// Dispose resources
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    await _bus.DisposeAsync();
  }
}
