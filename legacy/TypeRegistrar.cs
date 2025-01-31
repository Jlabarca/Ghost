using Ghost;
using Ghost.Legacy.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

public class TypeRegistrar : ITypeRegistrar
{
  private readonly IServiceCollection _services;
  private readonly GhostLogger _logger;

  public TypeRegistrar(IServiceCollection services, GhostLogger logger)
  {
    _services = services;
    _logger = logger;
  }

  public ITypeResolver Build()
  {
    return new TypeResolver(_services.BuildServiceProvider(), _logger);
  }

  public void Register(Type service, Type implementation)
  {
    _services.AddSingleton(service, implementation);
  }

  public void RegisterInstance(Type service, object implementation)
  {
    _services.AddSingleton(service, implementation);
  }

  public void RegisterLazy(Type service, Func<object> factory)
  {
    _services.AddSingleton(service, _ => factory());
  }
}
