using System.Collections.Concurrent;

namespace Ghost.Core.Storage.Cache;

/// <summary>
/// Thread-safe queue for channel messages
/// </summary>
internal class ChannelMessageQueue
{
    private readonly ConcurrentQueue<string> _queue = new();

    public void Enqueue(string message)
    {
        _queue.Enqueue(message);
    }

    public bool TryDequeue(out string message)
    {
        return _queue.TryDequeue(out message);
    }
}