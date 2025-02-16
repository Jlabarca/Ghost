// using Ghost.Infrastructure.Logging;
// using Microsoft.Extensions.Logging;
// namespace Ghost.SDK;
//
// /// <summary>
// /// Custom logging provider for Ghost apps
// /// </summary>
// internal class GhostLoggingProvider : ILoggerProvider
// {
//   private readonly GhostLogger _ghostLogger;
//
//   public GhostLoggingProvider(GhostLogger ghostLogger)
//   {
//     _ghostLogger = ghostLogger;
//   }
//
//   public ILogger CreateLogger(string categoryName)
//   {
//     return new GhostLoggerWrapper(_ghostLogger, categoryName);
//   }
//
//   public void Dispose()
//   {
//     // GhostLogger is disposed by the app
//   }
// }
