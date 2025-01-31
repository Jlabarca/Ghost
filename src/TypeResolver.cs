using Spectre.Console.Cli;

namespace Ghost;

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
        throw new InvalidOperationException($"Failed to resolve type: {type.FullName}");
      }
      return service;
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException($"Error resolving {type.Name}: {ex.Message}", ex);
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
