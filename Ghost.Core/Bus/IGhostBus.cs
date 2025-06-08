namespace Ghost.Storage;

/// <summary>
///     Message bus interface for pub/sub communication between Ghost components
/// </summary>
public interface IGhostBus : IAsyncDisposable
{
    /// <summary>
    ///     Publish a message to the specified channel
    /// </summary>
    /// <typeparam name="T">Message type</typeparam>
    /// <param name="channel">Channel name</param>
    /// <param name="message">Message object</param>
    /// <param name="expiry">Optional message expiry time</param>
    /// <returns>Task representing the operation</returns>
    Task PublishAsync<T>(string channel, T message, TimeSpan? expiry = null);

    /// <summary>
    ///     Subscribe to messages on the specified channel or pattern
    /// </summary>
    /// <typeparam name="T">Expected message type</typeparam>
    /// <param name="channelPattern">Channel name or pattern (supports * wildcard)</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>Async enumerable of messages</returns>
    IAsyncEnumerable<T> SubscribeAsync<T>(string channelPattern, CancellationToken cancellationToken = default(CancellationToken));

    /// <summary>
    ///     Unsubscribe from a channel or pattern
    /// </summary>
    /// <param name="channelPattern">Channel name or pattern to unsubscribe from</param>
    /// <returns>Task representing the operation</returns>
    Task UnsubscribeAsync(string channelPattern);

    /// <summary>
    ///     Check if the message bus is available
    /// </summary>
    /// <returns>True if the bus is available, false otherwise</returns>
    Task<bool> IsAvailableAsync();

    /// <summary>
    ///     Get the channel name from the last received message
    /// </summary>
    /// <returns>The channel name from the last message or null if none</returns>
    string GetLastTopic();
}
