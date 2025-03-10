using Spectre.Console.Cli;
using System.Text;

namespace Ghost.Father.CLI;

public class TypeResolver : ITypeResolver, IDisposable
{
    private readonly IServiceProvider _provider;
    private readonly HashSet<Type> _resolutionStack;

    public TypeResolver(IServiceProvider provider)
    {
        _provider = provider;
        _resolutionStack = new HashSet<Type>();
    }

    public object Resolve(Type type)
    {
        if (!_resolutionStack.Add(type))
        {
            throw new InvalidOperationException($"Circular dependency detected: {type.Name}");
        }

        var obj = ResolveInternal(type);
        _resolutionStack.Remove(type);
        return obj;
    }

    private object ResolveInternal(Type type)
    {
        var service = _provider.GetService(type);
        if (service != null)
        {
            return service;
        }

        var error = new StringBuilder();
        error.AppendLine($"Failed to resolve type: {type.FullName}");
        error.AppendLine($"Dependency chain: {string.Join(" -> ", _resolutionStack)}");

        var constructor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (constructor == null)
        {
            error.AppendLine("No public constructor found!");
            throw new InvalidOperationException(error.ToString()); // Throw immediately
        }

        error.AppendLine("\nConstructor dependencies:");
        foreach (var param in constructor.GetParameters())
        {
            var parameterType = param.ParameterType;
            var isRegistered = _provider.GetService(parameterType) != null;  // Check directly
            error.AppendLine($"  - {parameterType.Name}: {(isRegistered ? "✓" : "✗")}");

            if (!isRegistered)
            {
                AppendDependencyInfo(error, parameterType); // Helper for nested deps
            }
        }

        error.AppendLine("\nRegistration suggestions:");
        foreach (var param in constructor.GetParameters())
        {
            if (_provider.GetService(param.ParameterType) == null)
            {
                error.AppendLine($"  services.AddTransient<{param.ParameterType.Name}>();");

                if (param.ParameterType.IsInterface)
                {
                    AppendImplementations(error, param.ParameterType, type.Assembly);
                }
            }
        }

        throw new InvalidOperationException(error.ToString()); // Throw at the end
    }

    private void AppendDependencyInfo(StringBuilder error, Type parameterType)
    {
        var paramConstructor = parameterType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (paramConstructor == null) return; // No constructor, nothing to add

        error.AppendLine($"    {parameterType.Name}'s dependencies:");
        foreach (var subParam in paramConstructor.GetParameters())
        {
            var subIsRegistered = _provider.GetService(subParam.ParameterType) != null;
            error.AppendLine($"      - {subParam.ParameterType.Name}: {(subIsRegistered ? "✓" : "✗")}");
        }
    }

    private void AppendImplementations(StringBuilder error, Type interfaceType, System.Reflection.Assembly assembly)
    {
        var implementations = assembly.GetTypes()
            .Where(t => !t.IsAbstract && interfaceType.IsAssignableFrom(t))
            .Take(3);

        foreach (var impl in implementations)
        {
            error.AppendLine($"  // Or with implementation:");
            error.AppendLine($"  services.AddTransient<{interfaceType.Name}, {impl.Name}>();");
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