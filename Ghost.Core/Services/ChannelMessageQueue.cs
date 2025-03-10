using System.Collections.Concurrent;
namespace Ghost.Core.Storage;

public class ChannelMessageQueue
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