using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
using Ghost.SDK;
using System.Diagnostics;
namespace Ghost;

public static partial class GhostFather
{
    private static GhostAppBase _current;
    private static readonly object _lock = new();

    public static IGhostBus Bus => GetCurrent().Bus;
    public static IGhostData Data => GetCurrent().Data;
    public static GhostConfig Config => GetCurrent().Config;
    public static IAutoMonitor Monitor => GetCurrent().Metrics;

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