using System.Reflection;
using System.Text;
using Spectre.Console.Cli;
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


    public void Dispose()
    {
        if (_provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public object Resolve(Type type)
    {
        if (!_resolutionStack.Add(type))
        {
            throw new InvalidOperationException($"Circular dependency detected: {type.Name}");
        }

        object? obj = ResolveInternal(type);
        _resolutionStack.Remove(type);
        return obj;
    }

    private object ResolveInternal(Type type)
    {
        object? service = _provider.GetService(type);
        if (service != null)
        {
            return service;
        }

        StringBuilder? error = new StringBuilder();
        error.AppendLine($"Failed to resolve type: {type.FullName}");
        error.AppendLine($"Dependency chain: {string.Join(" -> ", _resolutionStack)}");

        ConstructorInfo? constructor = type.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

        if (constructor == null)
        {
            error.AppendLine("No public constructor found!");
            throw new InvalidOperationException(error.ToString()); // Throw immediately
        }

        error.AppendLine("\nConstructor dependencies:");
        foreach (ParameterInfo? param in constructor.GetParameters())
        {
            Type? parameterType = param.ParameterType;
            bool isRegistered = _provider.GetService(parameterType) != null; // Check directly
            error.AppendLine($"  - {parameterType.Name}: {(isRegistered ? "✓" : "✗")}");

            if (!isRegistered)
            {
                AppendDependencyInfo(error, parameterType); // Helper for nested deps
            }
        }

        error.AppendLine("\nRegistration suggestions:");
        foreach (ParameterInfo? param in constructor.GetParameters())
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
        ConstructorInfo? paramConstructor = parameterType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

        if (paramConstructor == null)
        {
            return; // No constructor, nothing to add
        }

        error.AppendLine($"    {parameterType.Name}'s dependencies:");
        foreach (ParameterInfo? subParam in paramConstructor.GetParameters())
        {
            bool subIsRegistered = _provider.GetService(subParam.ParameterType) != null;
            error.AppendLine($"      - {subParam.ParameterType.Name}: {(subIsRegistered ? "✓" : "✗")}");
        }
    }

    private void AppendImplementations(StringBuilder error, Type interfaceType, Assembly assembly)
    {
        var implementations = assembly.GetTypes()
                .Where(t => !t.IsAbstract && interfaceType.IsAssignableFrom(t))
                .Take(3);

        foreach (Type? impl in implementations)
        {
            error.AppendLine("  // Or with implementation:");
            error.AppendLine($"  services.AddTransient<{interfaceType.Name}, {impl.Name}>();");
        }
    }
}
