using Spectre.Console.Cli;
namespace Ghost.Father.CLI;

/// <summary>
///     Implements ITypeRegistrar for Spectre.Console, using an existing IServiceProvider
///     primarily for resolving types. Assumes commands are pre-registered in the IServiceProvider.
/// </summary>
internal sealed class SpectreServiceProviderAdapter : ITypeRegistrar
{
    private readonly IServiceProvider _serviceProvider;

    public SpectreServiceProviderAdapter(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public ITypeResolver Build()
    {
        return new SpectreTypeResolver(_serviceProvider);
    }

    // These methods are for Spectre.Console to register types.
    // Since our IServiceProvider is already built, we cannot add to it here.
    // All commands should be registered in GhostFatherCLI.ConfigureServices.
    public void Register(Type service, Type implementation)
    {
        // This would ideally add to an IServiceCollection before the provider is built.
        // For an existing IServiceProvider, this is a no-op or could log a warning.
        G.LogWarn($"SpectreServiceProviderAdapter: Attempted to register type '{implementation.FullName}' for service '{service.FullName}' after ServiceProvider was built. Ensure all commands are registered via IServiceCollection in ConfigureServices.");
    }

    public void RegisterInstance(Type service, object implementation)
    {
        G.LogWarn($"SpectreServiceProviderAdapter: Attempted to register instance for service '{service.FullName}' after ServiceProvider was built. This is not supported with a pre-built IServiceProvider.");
    }

    public void RegisterLazy(Type service, Func<object> factory)
    {
        G.LogWarn($"SpectreServiceProviderAdapter: Attempted to register lazy factory for service '{service.FullName}' after ServiceProvider was built. This is not supported with a pre-built IServiceProvider.");
    }
}
