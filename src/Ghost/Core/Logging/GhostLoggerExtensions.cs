using Ghost;
using Ghost.Core.Data;
using Ghost.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
public static class GhostLoggerExtensions
{
  public static IServiceCollection AddGhostLogger(
      this IServiceCollection services,
      ICache cache,  // Add cache parameter
      Action<GhostLoggerConfiguration>? configure = null)
  {
    var config = new GhostLoggerConfiguration();
    configure?.Invoke(config);

    // Register configuration
    services.AddSingleton(config);

    var logger = new GhostLogger(cache, config);

    // Register GhostLogger as implementation
    services.AddSingleton(logger);

    // Initialize G.Log
    G.Initialize(logger);

    // Register as ILogger interface
    services.AddSingleton<ILogger>(sp => sp.GetRequiredService<GhostLogger>());

    return services;
  }
}
