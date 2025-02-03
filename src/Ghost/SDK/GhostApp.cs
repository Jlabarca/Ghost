using Ghost.Core.Config;
namespace Ghost.SDK;

/// <summary>
/// Base class for one-off Ghost apps that run and exit
/// Think of this as a "task runner" - it does its job and finishes
/// </summary>
public abstract class GhostApp : GhostAppBase
{
  protected GhostApp(GhostOptions options = null) : base(options) { }

  /// <summary>
  /// Main execution method for the app. Override this to implement your app's logic.
  /// </summary>
  public abstract Task RunAsync();

  /// <summary>
  /// Runs a Ghost app of the specified type
  /// </summary>
  public static async Task RunAsync<T>(GhostOptions options = null) where T : GhostApp
  {
    await using var app = (T)Activator.CreateInstance(typeof(T), options);
    try
    {
      await app.InitializeAsync();
      await app.RunAsync();
    }
    finally
    {
      await app.ShutdownAsync();
    }
  }

  /// <summary>
  /// Runs a Ghost app of the specified type with explicit error handling
  /// </summary>
  public static async Task<bool> TryRunAsync<T>(GhostOptions options = null) where T : GhostApp
  {
    try
    {
      await RunAsync<T>(options);
      return true;
    }
    catch (Exception)
    {
      return false;
    }
  }
}
