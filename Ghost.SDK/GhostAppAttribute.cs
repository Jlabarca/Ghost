namespace Ghost;

/// <summary>
/// Attribute for configuring a Ghost application or service
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class GhostAppAttribute : Attribute
{
  /// <summary>
  /// Indicates if this is a long-running service
  /// </summary>
  public bool IsService { get; set; } = false;

  /// <summary>
  /// Should the app automatically connect to GhostFather
  /// </summary>
  public bool AutoGhostFather { get; set; } = true;

  /// <summary>
  /// Should the app automatically report metrics
  /// </summary>
  public bool AutoMonitor { get; set; } = true;

  /// <summary>
  /// Should the app automatically restart on failure
  /// </summary>
  public bool AutoRestart { get; set; } = false;

  /// <summary>
  /// Maximum number of restart attempts (0 = unlimited)
  /// </summary>
  public int MaxRestartAttempts { get; set; } = 3;

  /// <summary>
  /// Time between tick events for periodic processing (in seconds)
  /// </summary>
  public int TickIntervalSeconds { get; set; } = 5;
}
