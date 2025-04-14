using MessagePack;

namespace Ghost;

public static class GhostMessagePackResolver
{
  private static bool _initialized = false;
  private static readonly object _lock = new object();

  /// <summary>
  /// Ensures the MessagePack resolver is initialized
  /// </summary>
  public static void EnsureInitialized()
  {
    if (_initialized) return;

    lock (_lock)
    {
      if (_initialized) return;

      // Create a simpler resolver that should work with standard MessagePack versions
      var resolver = MessagePack.Resolvers.StandardResolver.Instance;

      var options = MessagePackSerializerOptions.Standard
          .WithSecurity(MessagePackSecurity.UntrustedData)
          .WithAllowAssemblyVersionMismatch(true)
          .WithResolver(resolver);

      MessagePackSerializer.DefaultOptions = options;
      _initialized = true;
    }
  }
}
