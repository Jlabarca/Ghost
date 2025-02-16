namespace Ghost.Core.Modules;

public class ModuleRegistry
{
  private readonly Dictionary<string, Type> _moduleTypes
      = new Dictionary<string, Type>();
  private readonly Dictionary<string, IGhostModule> _instances
      = new Dictionary<string, IGhostModule>();

  public void RegisterModule<TModule, TConfig>()
      where TModule : GhostModule<TConfig>
      where TConfig : ModuleConfig
  {
    var name = typeof(TModule).Name;
    _moduleTypes[name] = typeof(TModule);
  }

  public async Task<IGhostModule> CreateModuleAsync(
      string name,
      IGhostCore core,
      ModuleConfig config)
  {
    if (!_moduleTypes.TryGetValue(name, out var moduleType))
      throw new KeyNotFoundException($"Module {name} not registered");

    var module = (IGhostModule)Activator.CreateInstance(
        moduleType,
        new object[] { core, config }
    );

    await module.InitializeAsync();
    _instances[name] = module;

    return module;
  }

  public IGhostModule GetModule(string name)
  {
    return _instances.TryGetValue(name, out var module)
        ? module
        : throw new KeyNotFoundException($"Module {name} not initialized");
  }

  public IEnumerable<IGhostModule> GetAllModules() => _instances.Values;
}
