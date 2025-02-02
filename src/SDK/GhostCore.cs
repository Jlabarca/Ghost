using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Orchestration;
using Ghost.Infrastructure.Storage;
using Ghost.SDK.Monitoring;
using Ghost.SDK.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Ghost.SDK;

/// <summary>
/// Entry point for the Ghost SDK, providing a fluent interface for configuration and initialization
/// Think of this as the "lobby" of a building - it's where everything starts and directs you to where you need to go
/// </summary>
public class GhostCore
{
  private readonly IServiceCollection _services;
  private readonly GhostOptions _options;
  private IServiceProvider _provider;

  public GhostCore(Action<GhostOptions> configure = null)
  {
    _services = new ServiceCollection();
    _options = new GhostOptions();
    configure?.Invoke(_options);

    ConfigureServices();
  }
  public GhostOptions Options => _options;

  private void ConfigureServices()
  {
    // Register core services
    _services.AddSingleton(_options);
    _services.AddSingleton<IPermissionsManager, PermissionsManager>();
    _services.AddSingleton<ICoreAPI, CoreAPI>();
    _services.AddSingleton<IStateManager, StateManager>();
    _services.AddSingleton<IConfigClient, ConfigClient>();
    _services.AddSingleton<IDataAccess, DataAccess>();
    _services.AddSingleton<IAutoMonitor, AutoMonitor>();


    //define if redis or not
    if (_options.UseRedis)
    {
      _services.AddSingleton<IRedisClient, RedisClient>();
    }
    else
    {
      _services.AddSingleton<IRedisClient, LocalCacheClient>();
    }

    _services.AddSingleton<IRedisManager, RedisManager>();
    _services.AddSingleton<IConfigManager, ConfigManager>();
    _services.AddSingleton<IStorageRouter, StorageRouter>();
    _services.AddSingleton<IDataAPI, DataAPI>();
  }

  public IGhostApp Build()
  {
    _provider = _services.BuildServiceProvider();
    return new GhostApp(_provider);
  }
}
