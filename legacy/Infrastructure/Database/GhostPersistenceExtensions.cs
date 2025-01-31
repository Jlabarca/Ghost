
using Microsoft.Extensions.DependencyInjection;
namespace Ghost.Legacy.Infrastructure.Database;

// Extension methods for dependency injection
public static class GhostPersistenceExtensions
{
  public static IServiceCollection AddGhostPersistence(
      this IServiceCollection services,
      string appId,
      IDbProvider provider = null)
  {
    //get father path
    //var fatherPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    // Default to SQLite if no provider specified
    provider ??= new SqliteProvider();

    services.AddSingleton(provider);
    services.AddSingleton(sp => new GhostDatabase(
        provider,
        appId,
        sp.GetRequiredService<GhostLogger>()
    ));

    return services;
  }
}
