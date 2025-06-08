namespace Ghost.Modules;

public abstract class GhostModule<TConfig> : IGhostModule
        where TConfig : ModuleConfig
{
    protected readonly TConfig Config;

    protected GhostModule(TConfig config)
    {
        Config = config;
    }

    public string Name => GetType().Name;
    public bool IsEnabled => Config.Enabled;

    public virtual Task InitializeAsync()
    {
        return Task.CompletedTask;
    }
    public virtual Task StartAsync()
    {
        return Task.CompletedTask;
    }
    public virtual Task StopAsync()
    {
        return Task.CompletedTask;
    }

    public virtual IEnumerable<string> ValidateConfiguration()
    {
        return Config.Validate();
    }
}
