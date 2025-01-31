using Ghost.Legacy.Infrastructure;
using Spectre.Console.Cli;

public class TypeResolver : ITypeResolver, IDisposable
{
  private readonly IServiceProvider _provider;
  private readonly GhostLogger _logger;

  public TypeResolver(IServiceProvider provider, GhostLogger logger)
  {
    _provider = provider;
    _logger = logger;
  }

  public object Resolve(Type type)
  {
    try
    {
      var service = _provider.GetService(type);
      if (service == null)
      {
        _logger.Log("TypeResolver", $"Failed to resolve type: {type.FullName}");
        _logger.Log("TypeResolver", "Required constructor parameters:");
        foreach (var ctor in type.GetConstructors())
        {
          foreach (var param in ctor.GetParameters())
          {
            _logger.Log("TypeResolver", $"- {param.ParameterType.Name} {param.Name}");
          }
        }
      }
      return service;
    }
    catch (Exception ex)
    {
      _logger.Log("TypeResolver", $"Error resolving {type.Name}: {ex.Message}");
      throw;
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
