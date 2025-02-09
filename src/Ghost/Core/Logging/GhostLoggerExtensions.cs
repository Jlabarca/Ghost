using Ghost.Core.Storage.Cache;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ghost.Infrastructure.Logging;

public static class GhostLoggerExtensions
{
  public static IServiceCollection AddGhostLogger(
      this IServiceCollection services,
      ICache cache,
      Action<GhostLoggerConfiguration>? configure = null)
  {
    var config = new GhostLoggerConfiguration();
    configure?.Invoke(config);

    var logger = new GhostLogger(
        cache,
        config
    );

    services.AddSingleton(config);
    services.AddSingleton<ILogger>(logger);
    G.Initialize(logger);

    return services;
  }
}