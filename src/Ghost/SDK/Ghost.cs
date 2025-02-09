using Ghost.Core.Config;
using Ghost.Core.Storage;
using Ghost.Father;
using Ghost.SDK;

namespace Ghost;

public static partial class G
{
    private static GhostAppBase _current;
    private static readonly object _lock = new();

    public static IGhostBus Bus => GetCurrent().Bus;
    public static IGhostData Data => GetCurrent().Data;
    public static IGhostConfig Config => GetCurrent().Config;
    public static IAutoMonitor Metrics => GetCurrent().Metrics;

    private static GhostAppBase GetCurrent()
    {
        if (_current == null)
        {
            lock (_lock)
            {
                if (_current == null)
                {
                    throw new InvalidOperationException("No Ghost app is currently running");
                }
            }
        }
        return _current;
    }

    internal static void SetCurrent(GhostAppBase app)
    {
        lock (_lock)
        {
            _current = app ?? throw new ArgumentNullException(nameof(app));
        }
    }
}