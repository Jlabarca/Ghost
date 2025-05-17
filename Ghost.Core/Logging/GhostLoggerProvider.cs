using Ghost.Core.Data;
using Microsoft.Extensions.Logging;

namespace Ghost.Core.Logging;

public class GhostLoggerProvider : ILoggerProvider
{
  private readonly GhostLoggerConfiguration _config;
  private readonly ICache _cache;

  public GhostLoggerProvider(GhostLoggerConfiguration config, ICache cache)
  {
    _config = config;
    _cache = cache;
  }

  public ILogger CreateLogger(string categoryName)
  {
    return new GhostLoggerAdapter(_config, _cache);
  }

  public void Dispose() { /* cleanup */ }
}
