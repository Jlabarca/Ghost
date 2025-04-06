using Ghost.Core.Config;
using Ghost.Core.Data;
using Ghost.Core.Monitoring;
using Ghost.Core.Storage;
namespace Ghost.SDK
{
    /// <summary>
    /// Manages access to metrics collection and reporting
    /// </summary>
    public class MetricsManager
    {
        private readonly IGhostBus _bus;
        private readonly IGhostData _data;
        private readonly GhostConfig _config;
        private readonly MetricsCollector _collector;

        public MetricsManager(IGhostBus bus, IGhostData data, GhostConfig config)
        {
            _bus = bus;
            _data = data;
            _config = config;
            _collector = new MetricsCollector(_config.Core.MetricsInterval);
        }

        /// <summary>
        /// Track a metric value
        /// </summary>
        public async Task TrackAsync(string name, double value, Dictionary<string, string> tags = null)
        {
            await _collector.TrackMetricAsync(new MetricValue(
                name,
                value,
                tags ?? new Dictionary<string, string>(),
                DateTime.UtcNow
            ));
        }

        /// <summary>
        /// Track an event
        /// </summary>
        public async Task TrackEventAsync(string name, Dictionary<string, string> properties = null)
        {
            await _bus.PublishAsync($"ghost:events:{Ghost.Current?.Config?.App?.Id ?? "app"}", new
            {
                Name = name,
                Properties = properties ?? new Dictionary<string, string>(),
                Timestamp = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Get metrics for a specific time range
        /// </summary>
        public async Task<IEnumerable<MetricValue>> GetMetricsAsync(string name, DateTime start, DateTime end)
        {
            return await _collector.GetMetricsAsync(name, start, end);
        }
    }

}