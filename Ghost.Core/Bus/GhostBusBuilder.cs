// using Ghost.Storage;
// using IGhostBus = Ghost.Core.Messaging.IGhostBus;
// namespace Ghost.Bus;
//
// // Builder pattern for easier configuration
// public class GhostBusBuilder
// {
//   private string _redisConnectionString;
//   private bool _enableFallback = true;
//   private bool _enableDiagnostics = true;
//   private int _maxQueueSize = 1000;
//   private MessagePriority _minimumPersistPriority = MessagePriority.Normal;
//
//   public GhostBusBuilder UseRedis(string connectionString)
//   {
//     _redisConnectionString = connectionString;
//     return this;
//   }
//
//   public GhostBusBuilder WithFallback(bool enable = true)
//   {
//     _enableFallback = enable;
//     return this;
//   }
//
//   public GhostBusBuilder WithDiagnostics(bool enable = true)
//   {
//     _enableDiagnostics = enable;
//     return this;
//   }
//
//   public GhostBusBuilder WithQueueSize(int size)
//   {
//     _maxQueueSize = size;
//     return this;
//   }
//
//   public IGhostBus Build()
//   {
//     // Create and configure the bus
//     var storage = _enableFallback ? new FileSystemPersistentStorage() : null;
//     var diagnostics = _enableDiagnostics ? new DefaultConnectionDiagnostics() : null;
//
//     return new RedisGhostBus(_redisConnectionString, storage)
//     {
//         MaxQueueSize = _maxQueueSize,
//         MinimumPersistPriority = _minimumPersistPriority
//     };
//   }
// }