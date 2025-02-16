using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using System.Text;

namespace Ghost.Father.CLI;

public class TypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _provider;

    public TypeResolver(IServiceProvider provider)
    {
        _provider = provider;
    }

    public object Resolve(Type type)
    {
        try
        {
            var service = _provider.GetService(type);
            if (service == null)
            {
                var error = new StringBuilder();
                error.AppendLine($"Failed to resolve type: {type.FullName}");

                // Try to get constructor info
                var constructor = type.GetConstructors().FirstOrDefault();
                if (constructor != null)
                {
                    error.AppendLine("Constructor dependencies:");
                    foreach (var param in constructor.GetParameters())
                    {
                        var isRegistered = _provider.GetService(param.ParameterType) != null;
                        error.AppendLine($"  - {param.ParameterType.Name}: {(isRegistered ? "✓" : "✗ (not registered)")}");
                    }

                    // Check if any dependencies are missing
                    var missingDeps = constructor.GetParameters()
                        .Where(p => _provider.GetService(p.ParameterType) == null)
                        .ToList();

                    if (missingDeps.Any())
                    {
                        error.AppendLine("\nMissing registrations:");
                        foreach (var dep in missingDeps)
                        {
                            error.AppendLine($"  services.AddTransient<{dep.ParameterType.Name}>();");
                            // If it's an interface, try to find implementations
                            if (dep.ParameterType.IsInterface)
                            {
                                var implementations = type.Assembly.GetTypes()
                                    .Where(t => !t.IsAbstract && dep.ParameterType.IsAssignableFrom(t))
                                    .Take(3);

                                if (implementations.Any())
                                {
                                    error.AppendLine("  // Or use one of these implementations:");
                                    foreach (var impl in implementations)
                                    {
                                        error.AppendLine($"  services.AddTransient<{dep.ParameterType.Name}, {impl.Name}>();");
                                    }
                                }
                            }
                        }
                    }
                }

                throw new InvalidOperationException(error.ToString());
            }
            return service;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            var error = new StringBuilder();
            error.AppendLine($"Error resolving {type.Name}:");
            error.AppendLine(ex.Message);

            if (ex.InnerException != null)
            {
                error.AppendLine("\nInner exception:");
                error.AppendLine(ex.InnerException.Message);
            }

            throw new InvalidOperationException(error.ToString(), ex);
        }
    }

    public void Dispose()
    {
        if (_provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}