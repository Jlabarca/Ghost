namespace Ghost.Core.Storage;

/// <summary>
/// Message bus interface for pub/sub operations
/// </summary>
public interface IMessageBus : IStorageProvider 
{
  Task<long> PublishAsync(string channel, string message, CancellationToken ct = default);
  IAsyncEnumerable<string> SubscribeAsync(string channel, CancellationToken ct = default);
  Task<long> SubscriberCountAsync(string channel, CancellationToken ct = default);
  Task UnsubscribeAsync(string channel, CancellationToken ct = default);
}
