namespace Ghost.Core.Messaging;

/// <summary>
/// Enhanced message bus interface with support for various messaging patterns
/// </summary>
public interface IGhostBus : IAsyncDisposable
{
    // Standard pub/sub with optional delivery guarantee
    Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null) where T : class;
    IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, CancellationToken ct = default) where T : class;
    
    // Request/response pattern
    Task<TResponse?> RequestAsync<TRequest, TResponse>(
        string channel, 
        TRequest request, 
        TimeSpan timeout)
        where TRequest : class
        where TResponse : class;
    
    // Persistent messaging
    Task PublishPersistentAsync<T>(
        string channel, 
        T message, 
        TimeSpan? retention = null) where T : class;
    
    // Typed subscriptions with handlers
    IAsyncDisposable Subscribe<T>(
        string channel, 
        Func<T, CancellationToken, Task> handler) where T : class;
        
    // Unsubscribe from a channel
    Task UnsubscribeAsync(string channelPattern);
    
    // Check availability
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    
    // Get the channel name from the last received message
    string GetLastTopic();
}
