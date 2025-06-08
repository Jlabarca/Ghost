namespace Ghost.Modules;

public class ModuleRegistry
{
    private readonly Dictionary<string, IGhostModule> _instances
            = new Dictionary<string, IGhostModule>();
    private readonly Dictionary<string, Type> _moduleTypes
            = new Dictionary<string, Type>();

    public void RegisterModule<TModule, TConfig>()
            where TModule : GhostModule<TConfig>
            where TConfig : ModuleConfig
    {
        string? name = typeof(TModule).Name;
        _moduleTypes[name] = typeof(TModule);
    }

    public async Task<IGhostModule> CreateModuleAsync(
            string name,
            ModuleConfig config)
    {
        if (!_moduleTypes.TryGetValue(name, out Type? moduleType))
        {
            throw new KeyNotFoundException($"Module {name} not registered");
        }

        IGhostModule? module = (IGhostModule)Activator.CreateInstance(
                moduleType, config);

        await module.InitializeAsync();
        _instances[name] = module;

        return module;
    }

    public IGhostModule GetModule(string name)
    {
        return _instances.TryGetValue(name, out IGhostModule? module)
                ? module
                : throw new KeyNotFoundException($"Module {name} not initialized");
    }

    public IEnumerable<IGhostModule> GetAllModules()
    {
        return _instances.Values;
    }
}
