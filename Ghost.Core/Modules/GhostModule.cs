namespace Ghost.Core.Modules;

public abstract class GhostModule<TConfig> : IGhostModule
    where TConfig : ModuleConfig
{
  protected readonly TConfig Config;
  protected readonly IGhostCore Core;

  public string Name => GetType().Name;
  public bool IsEnabled => Config.Enabled;

  protected GhostModule(IGhostCore core, TConfig config)
  {
    Core = core;
    Config = config;
  }

  public virtual Task InitializeAsync() => Task.CompletedTask;
  public virtual Task StartAsync() => Task.CompletedTask;
  public virtual Task StopAsync() => Task.CompletedTask;

  public virtual IEnumerable<string> ValidateConfiguration()
  {
    return Config.Validate();
  }
}
