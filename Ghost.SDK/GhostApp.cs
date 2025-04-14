using Ghost.Core;
using Ghost.Core.Config;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Ghost;

/// <summary>
/// Defines the current state of a Ghost application
/// </summary>
public enum GhostAppState
{
  Created,
  Starting,
  Running,
  Stopping,
  Stopped,
  Failed
}

/// <summary>
/// Base class for Ghost applications and services
/// </summary>
public class GhostApp : IAsyncDisposable
{
  protected ServiceCollection Services { get; } = new ServiceCollection();

  /// <summary>
  /// Configuration for the app
  /// </summary>
  public GhostConfig Config { get; private set; }

  /// <summary>
  /// Access to the Ghost process and subsystems
  /// </summary>
  public GhostProcess GhostProcess { get; private set; }

  /// <summary>
  /// Connection to GhostFather monitoring system
  /// </summary>
  private GhostFatherConnection Connection { get; set; }

  /// <summary>
  /// Is this a long-running service
  /// </summary>
  public bool IsService { get; protected set; }

  /// <summary>
  /// Should the app automatically connect to GhostFather
  /// </summary>
  public bool AutoGhostFather { get; protected set; } = true;

  /// <summary>
  /// Should the app automatically report metrics
  /// </summary>
  public bool AutoMonitor { get; protected set; } = true;

  /// <summary>
  /// Should the app automatically restart on failure
  /// </summary>
  public bool AutoRestart { get; protected set; }

  /// <summary>
  /// Maximum number of restart attempts (0 = unlimited)
  /// </summary>
  public int MaxRestartAttempts { get; protected set; }

  /// <summary>
  /// Time between tick events for periodic processing
  /// </summary>
  public TimeSpan TickInterval { get; protected set; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Current state of the application
  /// </summary>
  public GhostAppState State { get; private set; } = GhostAppState.Created;

  /// <summary>
  /// Event fired when the application state changes
  /// </summary>
  public event EventHandler<GhostAppState> StateChanged;

  /// <summary>
  /// Event fired when an error occurs in the application
  /// </summary>
  public event EventHandler<Exception> ErrorOccurred;

  private CancellationTokenSource _cts = new CancellationTokenSource();
  private Timer _tickTimer;
  private int _restartAttempts = 0;
  private DateTime? _lastRestartTime;

  /// <summary>
  /// Constructor with optional configuration
  /// </summary>
  /// <param name="config">Configuration for the app</param>
  protected GhostApp(GhostConfig? config = null)
  {
    // Set default values
    Config = config ?? CreateDefaultConfig();

    // Initialize Ghost process
    GhostProcess = new GhostProcess();
    GhostProcess.Initialize(this);
    StartAsync().GetAwaiter().GetResult();

    // Apply settings from attributes and configuration
    ApplySettings();

    L.LogInfo($"Initialized GhostApp: {GetType().Name}");
  }


  /// <summary>
  /// Creates a new instance of a Ghost application
  /// </summary>
  /// <typeparam name="T">Type of application to create</typeparam>
  /// <param name="config">Optional configuration</param>
  /// <returns>A new instance of the specified app type</returns>
  public static T Create<T>(GhostConfig config = null) where T : GhostApp, new()
  {
    return new T()
    {
        Config = config ?? new T().LoadConfigFromYaml()
    };
  }

  /// <summary>
  /// Creates a builder for configuring a Ghost application
  /// </summary>
  /// <returns>A builder for configuration</returns>
  public static GhostAppBuilder CreateBuilder()
  {
    return new GhostAppBuilder();
  }

  /// <summary>
  /// Applies settings from attributes and configuration
  /// </summary>
  private void ApplySettings()
  {
    try
    {
      // First apply defaults from attributes
      var attribute = GetType().GetCustomAttribute<GhostAppAttribute>();
      if (attribute != null)
      {
        IsService = attribute.IsService;
        AutoGhostFather = attribute.AutoGhostFather;
        AutoMonitor = attribute.AutoMonitor;
        AutoRestart = attribute.AutoRestart;
        MaxRestartAttempts = attribute.MaxRestartAttempts;
        TickInterval = TimeSpan.FromSeconds(attribute.TickIntervalSeconds);
      }

      // Then override from configuration if available
      if (Config.Core != null)
      {
        // Check for autoGhostFather setting
        if (Config.Core.Settings.TryGetValue("autoGhostFather", out var autoGF))
        {
          AutoGhostFather = !string.Equals(autoGF, "false", StringComparison.OrdinalIgnoreCase);
        }

        // Check for autoMonitor setting
        if (Config.Core.Settings.TryGetValue("autoMonitor", out var autoMon))
        {
          AutoMonitor = !string.Equals(autoMon, "false", StringComparison.OrdinalIgnoreCase);
        }

        // Check for isService setting
        if (Config.Core.Settings.TryGetValue("isService", out var isService))
        {
          IsService = string.Equals(isService, "true", StringComparison.OrdinalIgnoreCase);
        }

        // Check for autoRestart setting
        if (Config.Core.Settings.TryGetValue("autoRestart", out var autoRestart))
        {
          AutoRestart = string.Equals(autoRestart, "true", StringComparison.OrdinalIgnoreCase);
        }

        // Check for maxRestartAttempts setting
        if (Config.Core.Settings.TryGetValue("maxRestartAttempts", out var maxRestarts) &&
            int.TryParse(maxRestarts, out var maxRestartsValue))
        {
          MaxRestartAttempts = maxRestartsValue;
        }

        // Check for tickInterval setting
        if (Config.Core.Settings.TryGetValue("tickInterval", out var tickInterval) &&
            int.TryParse(tickInterval, out var tickIntervalValue))
        {
          TickInterval = TimeSpan.FromSeconds(tickIntervalValue);
        }
      }

      // Initialize connection if auto-connect is enabled
      if (AutoGhostFather)
      {
        InitializeGhostFatherConnection();
      }
    }
    catch (Exception ex)
    {
      L.LogWarn($"Error applying app settings: {ex.Message}");
    }
  }

  /// <summary>
  /// Loads configuration from YAML file
  /// </summary>
  private GhostConfig LoadConfigFromYaml()
  {
    try
    {
      // Look for .ghost.yaml in the current directory
      var yamlPath = Path.Combine(Directory.GetCurrentDirectory(), ".ghost.yaml");
      if (File.Exists(yamlPath))
      {
        Console.WriteLine($"Loading config from: {yamlPath}");

        // Try to load and parse YAML
        try
        {
          return GhostConfig.LoadAsync(yamlPath).GetAwaiter().GetResult();
        }
        catch
        {
          // If parsing fails, create default config
          Console.WriteLine("Failed to parse YAML config, using defaults");
          return CreateDefaultConfig();
        }
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Failed to load config from .ghost.yaml: {ex.Message}");
    }

    // Return default config if no file found or error occurred
    return CreateDefaultConfig();
  }

  /// <summary>
  /// Creates default configuration for the application
  /// </summary>
  private GhostConfig CreateDefaultConfig()
  {
    var appName = GetType().Name;
    var directoryName = Path.GetFileName(Directory.GetCurrentDirectory());

    return new GhostConfig
    {
        App = new AppInfo
        {
            Id = string.IsNullOrEmpty(directoryName) ? appName.ToLowerInvariant() : directoryName,
            Name = appName,
            Description = $"{appName} - Ghost Application",
            Version = "1.0.0"
        },
        Core = new CoreConfig
        {
            Mode = "development",
            LogsPath = "logs",
            DataPath = "data"
        },
    };
  }

  /// <summary>
  /// Initializes the connection to GhostFather monitoring system
  /// </summary>
  private void InitializeGhostFatherConnection()
  {
    try
    {
      L.LogDebug("Initializing connection to GhostFather...");

      // Create metadata for the process
      var metadata = new ProcessMetadata(
          Name: Config.App.Name ?? GetType().Name,
          Type: IsService ? "service" : "app",
          Version: Config.App.Version ?? "1.0.0",
          Environment: new Dictionary<string, string>(),
          Configuration: new Dictionary<string, string>
          {
              ["AppType"] = IsService ? "service" : "one-shot"
          }
      );

      // Create connection
      Connection = new GhostFatherConnection(metadata);

      // Start reporting if auto-monitor is enabled
      if (AutoMonitor)
      {
        Connection.StartReporting();
      }

      L.LogInfo($"Connected to GhostFather: {Config.App.Id}");
    }
    catch (Exception ex)
    {
      L.LogWarn($"Failed to connect to GhostFather: {ex.Message}");
      // Create dummy connection that does nothing
      Connection = null;
    }
  }

  /// <summary>
  /// Starts the application asynchronously
  /// </summary>
  public async virtual Task StartAsync(IEnumerable<string> args = null)
  {
    try
    {
      // Prevent starting multiple times
      if (State != GhostAppState.Created && State != GhostAppState.Stopped && State != GhostAppState.Failed)
      {
        L.LogWarn($"Cannot start {GetType().Name} in state {State}");
        return;
      }

      // Update state and notify
      UpdateState(GhostAppState.Starting);

      // Create new cancellation token source
      _cts = new CancellationTokenSource();

      // Call lifecycle hooks
      await OnBeforeRunAsync();

      // Log start
      L.LogInfo($"Starting {GetType().Name}...");

      // Report running state
      await ReportHealthAsync("Starting", "Application is starting");

      // Start tick timer if this is a service
      if (IsService && TickInterval > TimeSpan.Zero)
      {
        _tickTimer = new Timer(OnTickCallback, null, TimeSpan.Zero, TickInterval);
      }

      // Update state
      UpdateState(GhostAppState.Running);

      // Run the application
      await RunAsync(args ?? Array.Empty<string>());

      // Report completed state for one-shot apps
      if (!IsService)
      {
        await ReportHealthAsync("Completed", "Application completed successfully");
      }

      // Call lifecycle hooks
      await OnAfterRunAsync();

      // Update state if still running (might have been stopped during OnAfterRunAsync)
      if (State == GhostAppState.Running)
      {
        UpdateState(GhostAppState.Stopped);
      }
    }
    catch (Exception ex)
    {
      L.LogError(ex, $"Error executing {GetType().Name}");

      // Update state
      UpdateState(GhostAppState.Failed);

      // Report error
      await ReportHealthAsync("Error", $"Application error: {ex.Message}");

      // Notify error event
      OnErrorOccurred(ex);

      // Call error handler
      await OnErrorAsync(ex);

      // Check if we should restart
      if (AutoRestart && (MaxRestartAttempts == 0 || _restartAttempts < MaxRestartAttempts))
      {
        await HandleAutoRestartAsync(ex);
      } else
      {
        // Rethrow for upper layers if not handling restart
        throw;
      }
    }
  }

  /// <summary>
  /// Handles auto-restart logic when an error occurs
  /// </summary>
  private async Task HandleAutoRestartAsync(Exception ex)
  {
    // Increment restart counter
    _restartAttempts++;

    // Reset counter if it's been a while since last restart
    var now = DateTime.UtcNow;
    if (_lastRestartTime.HasValue && (now - _lastRestartTime.Value).TotalMinutes > 5)
    {
      _restartAttempts = 1;
    }

    _lastRestartTime = now;

    // Calculate backoff with jitter
    var backoffSeconds = Math.Min(30, Math.Pow(2, _restartAttempts - 1));
    var jitter = new Random().NextDouble() * 0.5 + 0.75; // 75-125% of base delay
    var delayMs = (int)(backoffSeconds * 1000 * jitter);

    // Report restarting
    L.LogInfo($"Application failed. Restarting in {delayMs / 1000.0:0.0}s ({_restartAttempts}/{MaxRestartAttempts})");
    await ReportHealthAsync("Restarting", $"Restarting after error: {ex.Message}");

    // Wait before restart
    await Task.Delay(delayMs);

    // Restart
    await StartAsync();
  }

  /// <summary>
  /// Updates application state and triggers the StateChanged event
  /// </summary>
  private void UpdateState(GhostAppState newState)
  {
    if (State == newState) return;

    var oldState = State;
    State = newState;

    L.LogDebug($"App state changed: {oldState} → {newState}");
    StateChanged?.Invoke(this, newState);
  }

  /// <summary>
  /// Triggers the ErrorOccurred event
  /// </summary>
  private void OnErrorOccurred(Exception ex)
  {
    ErrorOccurred?.Invoke(this, ex);
  }

  /// <summary>
  /// Reports health status to GhostFather if connected and monitoring is enabled
  /// </summary>
  private async Task ReportHealthAsync(string status, string message)
  {
    if (Connection != null && AutoMonitor)
    {
      try
      {
        await Connection.ReportHealthAsync(status, message);
      }
      catch (Exception ex)
      {
        L.LogWarn($"Failed to report health status: {ex.Message}");
      }
    }
  }

  /// <summary>
  /// Main execution logic - override in derived classes
  /// </summary>
  public virtual Task RunAsync(IEnumerable<string> args)
  {
    L.LogInfo($"Running Ghost application: {GetType().Name}");
    return Task.CompletedTask;
  }

  /// <summary>
  /// Called before the main Run method
  /// </summary>
  protected virtual Task OnBeforeRunAsync()
  {
    return Task.CompletedTask;
  }

  /// <summary>
  /// Called after the main Run method
  /// </summary>
  protected virtual Task OnAfterRunAsync()
  {
    return Task.CompletedTask;
  }

  /// <summary>
  /// Called when an error occurs during execution
  /// </summary>
  protected virtual Task OnErrorAsync(Exception ex)
  {
    return Task.CompletedTask;
  }

  /// <summary>
  /// Called periodically for service apps
  /// </summary>
  protected virtual Task OnTickAsync()
  {
    return Task.CompletedTask;
  }

  /// <summary>
  /// Timer callback for service tick processing
  /// </summary>
  private async void OnTickCallback(object state)
  {
    // Skip if stopping or stopped
    if (State != GhostAppState.Running || _cts.IsCancellationRequested)
      return;

    try
    {
      // Call the tick handler
      await OnTickAsync();

      // Report metrics
      if (Connection != null && AutoMonitor)
      {
        await Connection.ReportMetricsAsync();
      }
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Error in tick callback");

      // Don't crash the app for tick errors, but notify
      OnErrorOccurred(ex);
    }
  }

  /// <summary>
  /// Stop the application
  /// </summary>
  public virtual async Task StopAsync()
  {
    // Skip if already stopped
    if (State == GhostAppState.Stopped || State == GhostAppState.Stopping)
      return;

    try
    {
      // Update state
      UpdateState(GhostAppState.Stopping);

      L.LogInfo($"Stopping {GetType().Name}...");

      // Cancel any operations
      _cts.Cancel();

      // Stop tick timer
      if (_tickTimer != null)
      {
        await _tickTimer.DisposeAsync();
        _tickTimer = null;
      }

      // Report stopping state
      await ReportHealthAsync("Stopping", "Application is stopping");

      // Update state
      UpdateState(GhostAppState.Stopped);
    }
    catch (Exception ex)
    {
      L.LogError(ex, $"Error stopping {GetType().Name}");

      // Update state to failed
      UpdateState(GhostAppState.Failed);

      // Notify error
      OnErrorOccurred(ex);

      // Rethrow
      throw;
    }
  }

  /// <summary>
  /// Cleanup resources
  /// </summary>
  public async virtual ValueTask DisposeAsync()
  {
    // Stop the application if it's still running
    try
    {
      if (State == GhostAppState.Running || State == GhostAppState.Starting)
      {
        await StopAsync();
      }
    }
    catch (Exception ex)
    {
      L.LogError(ex, "Error stopping application during dispose");
    }

    // Dispose connection
    if (Connection != null)
    {
      try
      {
        await Connection.DisposeAsync();
      }
      catch (Exception ex)
      {
        L.LogError(ex, "Error disposing GhostFather connection");
      }
      finally
      {
        Connection = null;
      }
    }

    // Dispose cancellation token source
    _cts.Dispose();

    // Dispose Ghost process
    if (GhostProcess is IAsyncDisposable disposable)
    {
      await disposable.DisposeAsync();
    }
  }

  /// <summary>
  /// Executes the application and returns when completed
  /// </summary>
  public async Task ExecuteAsync(IEnumerable<string> args = null)
  {
    await StartAsync(args);

    // For services, wait until explicitly stopped
    if (IsService)
    {
      // Create a task completion source that never completes unless cancelled
      var tcs = new TaskCompletionSource<bool>();

      // Set up cancellation to release the wait
      using var cts = new CancellationTokenSource();
      cts.Token.Register(() => tcs.TrySetResult(true));

      // Wait for cancellation
      await tcs.Task;
    }
  }
}

/// <summary>
/// Builder for configuring and creating Ghost applications
/// </summary>
public class GhostAppBuilder
{
  private Type _appType;
  private GhostConfig _config;
  private bool _isService;
  private bool _autoGhostFather = true;
  private bool _autoMonitor = true;
  private bool _autoRestart;
  private int _maxRestartAttempts = 3;
  private TimeSpan _tickInterval = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Specifies the app type to create
  /// </summary>
  public GhostAppBuilder UseApp<T>() where T : GhostApp, new()
  {
    _appType = typeof(T);
    return this;
  }

  /// <summary>
  /// Sets the configuration for the app
  /// </summary>
  public GhostAppBuilder WithConfig(GhostConfig config)
  {
    _config = config;
    return this;
  }

  /// <summary>
  /// Configures the app as a service
  /// </summary>
  public GhostAppBuilder AsService(bool isService = true)
  {
    _isService = isService;
    return this;
  }

  /// <summary>
  /// Configures auto-connection to GhostFather
  /// </summary>
  public GhostAppBuilder WithGhostFather(bool autoConnect = true)
  {
    _autoGhostFather = autoConnect;
    return this;
  }

  /// <summary>
  /// Configures auto-monitoring
  /// </summary>
  public GhostAppBuilder WithMonitoring(bool autoMonitor = true)
  {
    _autoMonitor = autoMonitor;
    return this;
  }

  /// <summary>
  /// Configures auto-restart behavior
  /// </summary>
  public GhostAppBuilder WithAutoRestart(bool autoRestart = true, int maxAttempts = 3)
  {
    _autoRestart = autoRestart;
    _maxRestartAttempts = maxAttempts;
    return this;
  }

  /// <summary>
  /// Sets the tick interval for services
  /// </summary>
  public GhostAppBuilder WithTickInterval(TimeSpan interval)
  {
    _tickInterval = interval;
    return this;
  }

  /// <summary>
  /// Builds the configured app
  /// </summary>
  public GhostApp Build()
  {
    if (_appType == null)
    {
      throw new InvalidOperationException("App type must be specified with UseApp<T>()");
    }

    // Create instance
    var app = (GhostApp)Activator.CreateInstance(_appType);

    // Apply configuration if provided
    // if (_config != null)
    // {
    //   app.Config = _config;
    // }
    //
    // // Apply settings
    // app.IsService = _isService;
    // app.AutoGhostFather = _autoGhostFather;
    // app.AutoMonitor = _autoMonitor;
    // app.AutoRestart = _autoRestart;
    // app.MaxRestartAttempts = _maxRestartAttempts;
    // app.TickInterval = _tickInterval;

    return app;
  }
}