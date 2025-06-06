namespace Ghost.Storage.Metrics
{
    public interface IGhostBusMetrics
    {
        void IncrementMessagePublished(string channel);
        void IncrementMessageReceived(string channel);
        void RecordPublishLatency(string channel, TimeSpan duration);
        void RecordSubscriptionLatency(string channel, TimeSpan duration);
        void IncrementErrors(string operation, string channel);
        void UpdateActiveSubscriptions(int count);
        void UpdateActiveChannels(int count);
        Dictionary<string, object> GetMetricsSnapshot();
    }

}