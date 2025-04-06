using System.Text.Json.Serialization;
namespace Ghost.Father.Ghost.Core.Monitoring;

/// <summary>
/// Registration information for a process to be managed by GhostFather
/// </summary>
public class ProcessRegistration
{
  /// <summary>
  /// Unique identifier for the process
  /// </summary>
  public string Id { get; set; }

  /// <summary>
  /// Display name for the process
  /// </summary>
  public string Name { get; set; }

  /// <summary>
  /// Type of process (app, service, daemon, wrapped, etc.)
  /// </summary>
  public string Type { get; set; }

  /// <summary>
  /// Version of the process
  /// </summary>
  public string Version { get; set; }

  /// <summary>
  /// Full path to the executable
  /// </summary>
  public string ExecutablePath { get; set; }

  /// <summary>
  /// Command line arguments
  /// </summary>
  public string Arguments { get; set; }

  /// <summary>
  /// Working directory for the process
  /// </summary>
  public string WorkingDirectory { get; set; }

  /// <summary>
  /// Environment variables to set for the process
  /// </summary>
  public Dictionary<string, string> Environment { get; set; } = new Dictionary<string, string>();

  /// <summary>
  /// Configuration parameters for the process
  /// </summary>
  public Dictionary<string, string> Configuration { get; set; } = new Dictionary<string, string>();

  /// <summary>
  /// Whether the process should automatically restart if it exits
  /// </summary>
  [JsonIgnore]
  public bool AutoRestart => Configuration.TryGetValue("autoRestart", out var value) &&
                             bool.TryParse(value, out var result) && result;

  /// <summary>
  /// Maximum number of restart attempts (0 = unlimited)
  /// </summary>
  [JsonIgnore]
  public int MaxRestartAttempts => Configuration.TryGetValue("maxRestartAttempts", out var value) &&
                                   int.TryParse(value, out var result) ? result : 3;

  /// <summary>
  /// Delay in milliseconds before restarting the process
  /// </summary>
  [JsonIgnore]
  public int RestartDelayMs => Configuration.TryGetValue("restartDelayMs", out var value) &&
                               int.TryParse(value, out var result) ? result : 5000;

  /// <summary>
  /// Whether to watch for file changes and restart the process
  /// </summary>
  [JsonIgnore]
  public bool Watch => Configuration.TryGetValue("watch", out var value) &&
                       bool.TryParse(value, out var result) && result;

  /// <summary>
  /// Whether this process is a long-running service (vs a one-shot application)
  /// </summary>
  [JsonIgnore]
  public bool IsService => Configuration.TryGetValue("AppType", out var appType) &&
                           string.Equals(appType, "service", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// Create an empty process registration
  /// </summary>
  public ProcessRegistration()
  {
  }

  /// <summary>
  /// Create a process registration with specified values
  /// </summary>
  public ProcessRegistration(
      string id,
      string name,
      string type,
      string version,
      string executablePath,
      string arguments = "",
      string workingDirectory = null,
      Dictionary<string, string> environment = null,
      Dictionary<string, string> configuration = null)
  {
    Id = id;
    Name = name;
    Type = type;
    Version = version;
    ExecutablePath = executablePath;
    Arguments = arguments;
    WorkingDirectory = workingDirectory ?? System.IO.Path.GetDirectoryName(executablePath);
    Environment = environment ?? new Dictionary<string, string>();
    Configuration = configuration ?? new Dictionary<string, string>();
  }

  /// <summary>
  /// Get the full command line for the process
  /// </summary>
  public string GetCommandLine()
  {
    return string.IsNullOrEmpty(Arguments)
        ? ExecutablePath
        : $"{ExecutablePath} {Arguments}";
  }

  /// <summary>
  /// Clone the registration with a new ID
  /// </summary>
  public ProcessRegistration Clone(string newId = null)
  {
    var clone = new ProcessRegistration
    {
        Id = newId ?? Id,
        Name = Name,
        Type = Type,
        Version = Version,
        ExecutablePath = ExecutablePath,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory,
        Environment = new Dictionary<string, string>(Environment),
        Configuration = new Dictionary<string, string>(Configuration)
    };

    return clone;
  }
}
