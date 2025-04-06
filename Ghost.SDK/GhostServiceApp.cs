namespace Ghost.SDK;

/// <summary>
/// Base class for long-running Ghost service applications
/// </summary>
public abstract class GhostServiceApp : GhostApp
{
  /// <summary>
  /// Creates a new Ghost service application
  /// </summary>
  protected GhostServiceApp()
  {
    // Default settings for services
    TickInterval = TimeSpan.FromSeconds(1);
    AutoRestart = true;
    MaxRestartAttempts = 3;
    RestartDelay = TimeSpan.FromSeconds(5);
  }

  /// <summary>
  /// Main execution method for services. This runs once to initialize the service,
  /// then the service continues running with regular calls to OnTickAsync.
  /// </summary>
  /// <param name="args">Command-line arguments</param>
  protected async Task ExecuteAsync(string[] args)
  {
    // For services, this just needs to initialize the service state
    // The actual work happens in the tick callback

    G.LogInfo($"{GetType().Name} service initialized");

    // Allow derived classes to perform their own initialization
    await InitializeServiceAsync(args);
  }

  /// <summary>
  /// Called once when the service starts. Override to add service initialization logic.
  /// </summary>
  /// <param name="args">Command-line arguments</param>
  protected virtual Task InitializeServiceAsync(string[] args)
  {
    return Task.CompletedTask;
  }
}
