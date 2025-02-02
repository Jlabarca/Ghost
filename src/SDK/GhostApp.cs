using Ghost.Infrastructure.Monitoring;
using Ghost.SDK.Monitoring;
using Ghost.SDK.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Ghost.SDK;

/// <summary>
/// Implementation of the Ghost application interface
/// This is like the "building management system" that coordinates all the different subsystems
/// </summary>
internal class GhostApp : IGhostApp
{
  private readonly IServiceProvider _provider;
  private bool _isStarted;

  public ICoreAPI API { get; }
  public IStateManager State { get; }
  public IConfigClient Config { get; }
  public IDataAccess Data { get; }
  public IAutoMonitor Monitor { get; }

  public GhostApp(IServiceProvider provider)
  {
    _provider = provider;
    API = provider.GetRequiredService<ICoreAPI>();
    State = provider.GetRequiredService<IStateManager>();
    Config = provider.GetRequiredService<IConfigClient>();
    Data = provider.GetRequiredService<IDataAccess>();
    Monitor = provider.GetRequiredService<IAutoMonitor>();
  }

  public async Task StartAsync()
  {
    if (_isStarted) return;

    // Start monitoring if enabled
    var options = _provider.GetRequiredService<GhostOptions>();
    if (options.EnableMetrics)
    {
      await Monitor.StartAsync(options.MetricsInterval);
    }

    _isStarted = true;
  }

  public async Task StopAsync()
  {
    if (!_isStarted) return;

    await Monitor.StopAsync();
    _isStarted = false;
  }

  public async ValueTask DisposeAsync()
  {
    await StopAsync();
    if (_provider is IAsyncDisposable disposable)
    {
      await disposable.DisposeAsync();
    }
  }
}
/// <summary>
/// The main interface for interacting with a Ghost application
/// Think of this as your "control panel" for the entire system
/// </summary>
public interface IGhostApp : IAsyncDisposable
{
  ICoreAPI API { get; }
  IStateManager State { get; }
  IConfigClient Config { get; }
  IDataAccess Data { get; }
  IAutoMonitor Monitor { get; }

  Task StartAsync();
  Task StopAsync();
}
/// <summary>
/// Core API interface providing high-level operations for Ghost applications
/// Think of this as the "reception desk" - it's the main point of contact for common operations
/// </summary>
public interface ICoreAPI
{
  Task<T> InvokeOperationAsync<T>(string operationType, object parameters);
  Task PublishEventAsync(string eventType, object payload);
  Task<bool> IsHealthyAsync();
  Task<SystemMetrics> GetSystemMetricsAsync();
}
public class SystemMetrics
{
  public string Status { get; set; }
  public DateTime LastUpdate { get; set; }
  public Dictionary<string, ProcessMetrics> Processes { get; set; }
}
