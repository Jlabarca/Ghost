using Ghost;
using Ghost.Core.Data;
using Ghost.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ghost.Core.Logging;

/// <summary>
/// Extension methods for registering the DefaultGhostLogger
/// </summary>
public static class GhostLoggerExtensions
{
  /// <summary>
  /// Adds the DefaultGhostLogger to the service collection
  /// </summary>
  public static IServiceCollection AddGhostLogger(
      this IServiceCollection services,
      ICache cache,
      Action<GhostLoggerConfiguration>? configure = null)
  {
    var config = new GhostLoggerConfiguration();
    configure?.Invoke(config);

    // Register configuration
    services.AddSingleton(config);

    // Create and register the DefaultGhostLogger
    var logger = new DefaultGhostLogger(cache, config);

    // Register DefaultGhostLogger as implementation
    services.AddSingleton(logger);

    // Initialize L.Log
    L.Initialize(logger);

    // Register as standard ILogger interface
    services.AddSingleton<ILogger>(sp => sp.GetRequiredService<DefaultGhostLogger>());

    // Register as IGhostLogger interface
    services.AddSingleton<IGhostLogger>(sp => sp.GetRequiredService<DefaultGhostLogger>());

    return services;
  }
}
