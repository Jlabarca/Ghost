using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Orchestration;
using Ghost.Infrastructure.Storage;
using Ghost.Infrastructure.Storage.Database;
using Ghost.SDK;
using Microsoft.Extensions.DependencyInjection;
namespace Ghost;

public static class ServiceCollectionExtensions
{
  public static IServiceCollection AddGhostInfrastructure(this IServiceCollection services, GhostOptions options)
  {
    // Register storage services
    if (options.UseRedis)
    {
      services.AddSingleton<IRedisClient>(sp =>
          new RedisClient(options.RedisConnectionString));
    }
    else
    {
      services.AddSingleton<IRedisClient>(sp =>
          new LocalCacheClient(Path.Combine(options.DataDirectory, "cache")));
    }

    // Register database services
    if (options.UsePostgres)
    {
      services.AddSingleton<IDatabaseClient>(sp =>
          new PostgresClient(options.PostgresConnectionString));
    }
    else
    {
      var sqlitePath = Path.Combine(options.DataDirectory, "ghost.db");
      var connectionString = $"Data Source={sqlitePath}";
      services.AddSingleton<IDatabaseClient>(sp =>
          new SQLiteClient(connectionString));
    }

    // Register core infrastructure services
    services.AddSingleton<IStorageRouter, StorageRouter>();
    services.AddSingleton<IPermissionsManager, PermissionsManager>();
    services.AddSingleton<IDataAPI, DataAPI>();
    services.AddSingleton<IRedisManager, RedisManager>();

    // Register configuration services
    services.AddSingleton<IConfigManager, ConfigManager>();

    return services;
  }
}