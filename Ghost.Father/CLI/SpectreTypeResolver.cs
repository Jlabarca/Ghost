using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
namespace Ghost.Father.CLI;

internal sealed class SpectreTypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _provider;
    private readonly IServiceScope _scope; // Optional: For scoped command execution

    public SpectreTypeResolver(IServiceProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        // If your commands need scoped services (e.g., DbContext), create a scope.
        // _scope = _provider.CreateScope(); 
    }

    public void Dispose()
    {
        _scope?.Dispose();
    }

    public object Resolve(Type type)
    {
        if (type == null)
        {
            return null;
        }
        // If using a scope: return _scope.ServiceProvider.GetService(type);
        return _provider.GetService(type); // Or GetRequiredService if null should throw
    }
}
