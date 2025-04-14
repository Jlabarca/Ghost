using Ghost.Core.Exceptions;
using MemoryPack;
using System.Diagnostics;
namespace Ghost.Core;

[MemoryPackable]
public partial class ProcessInfo : IAsyncDisposable
{
  public string Id { get; set; } = string.Empty;
  public ProcessMetadata Metadata { get; set; } = default!; // Ensure set before serializing or handle null
  public string ExecutablePath { get; set; } = string.Empty;
  public string Arguments { get; set; } = string.Empty;
  public string WorkingDirectory { get; set; } = string.Empty;
  public int MaxBufferSize { get; set; }

  [MemoryPackInclude]
  public ProcessStatus Status { get; internal set; }
  [MemoryPackInclude]
  public DateTime StartTime { get; internal set; }
  [MemoryPackInclude]
  public DateTime? StopTime { get; internal set; }
  [MemoryPackInclude]
  public int RestartCount { get; internal set; }

  [MemoryPackInclude]
  internal string? LastErrorString { get; private set; }


  [MemoryPackIgnore] // Replaced JsonIgnore
  public bool IsRunning => Status == ProcessStatus.Running;

  [MemoryPackIgnore] // Replaced JsonIgnore
  public TimeSpan Uptime => IsRunning ? (DateTime.UtcNow - StartTime) : (StopTime.HasValue ? (StopTime.Value - StartTime) : TimeSpan.Zero);

  [MemoryPackIgnore]
  public Exception? LastError { get; private set; }

  [MemoryPackIgnore]
  private Process? _process;
  [MemoryPackIgnore]
  private ProcessStartInfo _startInfo = default!; // Reconstructed OnDeserialized
  [MemoryPackIgnore]
  private readonly SemaphoreSlim _lock; // Cannot be serialized
  [MemoryPackIgnore]
  private readonly List<string> _outputBuffer; // Runtime buffer
  [MemoryPackIgnore]
  private readonly List<string> _errorBuffer;  // Runtime buffer
  [MemoryPackIgnore]
  private bool _isDisposed;

  public event EventHandler<ProcessOutputEventArgs>? OutputReceived;
  public event EventHandler<ProcessOutputEventArgs>? ErrorReceived;
  public event EventHandler<ProcessStatusEventArgs>? StatusChanged;

  [MemoryPackConstructor]
  public ProcessInfo()
  {
    // Initialize non-serialized fields required for runtime operation
    _lock = new SemaphoreSlim(1, 1);
    _outputBuffer = new List<string>();
    _errorBuffer = new List<string>();

    // Sensible defaults for serialized properties
    Id = string.Empty;
    Metadata = new ProcessMetadata("", "", "", new Dictionary<string, string>(), new Dictionary<string, string>()); // Default metadata
    ExecutablePath = string.Empty;
    Arguments = string.Empty;
    WorkingDirectory = string.Empty;
    MaxBufferSize = 1000; // Default buffer size
    Status = ProcessStatus.Stopped;
  }

  public ProcessInfo(
      string id,
      ProcessMetadata metadata,
      string executablePath,
      string arguments,
      string workingDirectory,
      Dictionary<string, string> environment,
      int maxBufferSize = 1000) : this() // Chain constructor
  {
    Id = id ?? throw new ArgumentNullException(nameof(id));
    Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    ExecutablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
    Arguments = arguments ?? string.Empty;
    WorkingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    MaxBufferSize = maxBufferSize; // Use the public property now

    // Configure the non-serialized _startInfo (runtime only)
    ConfigureStartInfo(environment);

    Status = ProcessStatus.Stopped; // Initial status
    L.LogDebug("Created ProcessInfo: {0} ({1})", Id, metadata.Name);
  }

  private void ConfigureStartInfo(Dictionary<string, string>? environment = null)
  {
    _startInfo = new ProcessStartInfo
    {
        FileName = ExecutablePath,
        Arguments = Arguments,
        WorkingDirectory = WorkingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    // Apply environment variables from metadata
    if (Metadata?.Environment != null)
    {
      foreach (var (key, value) in Metadata.Environment)
      {
        _startInfo.EnvironmentVariables[key] = value;
      }
    }
    // Apply environment variables passed directly (only during initial construction)
    if (environment != null)
    {
      foreach (var (key, value) in environment)
      {
        _startInfo.EnvironmentVariables[key] = value;
      }
    }
  }

  // Helper to manage setting the LastError and its serializable string form
  private void SetLastError(Exception? ex)
  {
    LastError = ex;
    LastErrorString = ex?.ToString(); // Capture string representation for serialization
  }

  [MemoryPackOnDeserialized]
  internal void OnDeserialized()
  {
    // Reconstruct non-serialized state needed after deserialization
    ConfigureStartInfo(); // Rebuild _startInfo from deserialized properties

    // Optionally, try to reconstruct a basic Exception from the string
    // if (!string.IsNullOrEmpty(LastErrorString))
    // {
    //     LastError = new Exception($"Deserialized error: {LastErrorString}");
    // }
  }

  public async Task StartAsync()
  {
    await _lock.WaitAsync();
    try
    {
      if (_isDisposed) throw new ObjectDisposedException(nameof(ProcessInfo));
      if (Status == ProcessStatus.Running)
      {
        // L.LogWarn("Process already running: {0}", Id);
        return;
      }

      // Ensure _startInfo is configured (important after deserialization)
      if (_startInfo == null || string.IsNullOrEmpty(_startInfo.FileName))
      {
        ConfigureStartInfo();
      }

      await UpdateStatusAsync(ProcessStatus.Starting);
      try
      {
        _process = new Process { StartInfo = _startInfo };
        ConfigureProcessCallbacks(); // Make sure callbacks are set AFTER new Process()
        if (!_process.Start())
        {
          throw new GhostException($"Failed to start process {Id}", ErrorCode.ProcessStartFailed);
        }

        StartTime = DateTime.UtcNow;
        StopTime = null; // Reset stop time on start
        SetLastError(null); // Clear last error on successful start
        await UpdateStatusAsync(ProcessStatus.Running);

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // L.LogInfo("Started process: {0}", Id);
      }
      catch (Exception ex)
      {
        SetLastError(ex); // Use helper
        await UpdateStatusAsync(ProcessStatus.Failed);
        // L.LogError("Failed to start process: {0}", ex, Id);
        throw; // Re-throw original exception
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task StopAsync(TimeSpan timeout)
  {
    await _lock.WaitAsync();
    try
    {
      if (_isDisposed) throw new ObjectDisposedException(nameof(ProcessInfo));
      // Check Status instead of just _process, as it might be Failed/Crashed
      if (Status == ProcessStatus.Stopped || Status == ProcessStatus.Stopping)
      {
        return;
      }

      // If process doesn't exist or has already exited externally
      if (_process == null || _process.HasExited)
      {
        StopTime = DateTime.UtcNow;
        await UpdateStatusAsync(ProcessStatus.Stopped);
        // L.LogInfo("Process already exited or not running: {0}", Id);
        return;
      }

      await UpdateStatusAsync(ProcessStatus.Stopping);
      try
      {
        if (!_process.HasExited)
        {
          // Attempt graceful shutdown first (may not work for all apps)
          try { _process.CloseMainWindow(); } catch { /* Ignore errors */ }

          // Wait for exit or timeout
          if (!_process.WaitForExit((int)timeout.TotalMilliseconds))
          {
            // L.LogWarn("Process {0} did not exit gracefully, killing.", Id);
            try { _process.Kill(entireProcessTree: true); } catch { /* Ignore errors */ }
            // Need to wait a bit after kill, or call WaitForExit again briefly
            _process.WaitForExit(1000); // Wait briefly after kill
          }
        }

        StopTime = DateTime.UtcNow;
        await UpdateStatusAsync(ProcessStatus.Stopped);
        // L.LogInfo("Stopped process: {0}", Id);
      }
      catch (Exception ex)
      {
        SetLastError(ex); // Use helper
        // Consider setting status to Failed if stop fails unexpectedly
        await UpdateStatusAsync(ProcessStatus.Failed);
        // L.LogError("Failed to stop process: {0}", ex, Id);
        // Avoid throwing here unless absolutely necessary, allow manager to decide
        // throw new GhostException($"Failed to stop process {Id}", ex, ErrorCode.ProcessError);
      }
      finally
      {
        // Clean up process object after stopping
        _process?.Dispose();
        _process = null;
      }
    }
    finally
    {
      _lock.Release();
    }
  }

  public async Task RestartAsync(TimeSpan timeout)
  {
    // Ensure Stop completes before starting again
    await StopAsync(timeout);
    // Small delay might be beneficial sometimes
    await Task.Delay(100);
    await StartAsync();
    RestartCount++;
    // L.LogInfo("Restarted process: {0} (restart count: {1})", Id, RestartCount);
  }


  private void ConfigureProcessCallbacks()
  {
    if (_process == null) return;

    _process.EnableRaisingEvents = true; // Required for Exited event

    _process.OutputDataReceived += HandleOutputData;
    _process.ErrorDataReceived += HandleErrorData;
    _process.Exited += HandleProcessExited;
  }

  // Separate handlers for clarity
  private void HandleOutputData(object sender, DataReceivedEventArgs e)
  {
    if (!string.IsNullOrEmpty(e.Data))
    {
      lock (_outputBuffer)
      {
        _outputBuffer.Add(e.Data);
        while (_outputBuffer.Count > MaxBufferSize) // Use property
        {
          _outputBuffer.RemoveAt(0);
        }
      }
      OutputReceived?.Invoke(this, new ProcessOutputEventArgs(e.Data));
    }
  }

  private void HandleErrorData(object sender, DataReceivedEventArgs e)
  {
    if (!string.IsNullOrEmpty(e.Data))
    {
      lock (_errorBuffer)
      {
        _errorBuffer.Add(e.Data);
        while (_errorBuffer.Count > MaxBufferSize) // Use property
        {
          _errorBuffer.RemoveAt(0);
        }
      }
      ErrorReceived?.Invoke(this, new ProcessOutputEventArgs(e.Data));
    }
  }

  // Use async void carefully, only for top-level event handlers
  private async void HandleProcessExited(object? sender, EventArgs e)
  {
    // Ensure lock is acquired to prevent race conditions with StopAsync/DisposeAsync
    await _lock.WaitAsync();
    try
    {
      // Check if we are already stopping/stopped to avoid double processing
      if (Status == ProcessStatus.Stopping || Status == ProcessStatus.Stopped)
      {
        return;
      }

      int exitCode = -1;
      try
      {
        // Process might be disposed if StopAsync was called concurrently
        if (_process != null && !_process.HasExited) { /* Should not happen if Exited fired */ }
        if (_process != null) exitCode = _process.ExitCode;
      }
      catch (InvalidOperationException) { /* Process already gone */ }
      catch (Exception ex) { /* L.LogError("Error getting exit code for {0}: {1}", Id, ex.Message); */ }


      if (exitCode != 0)
      {
        SetLastError(new GhostException($"Process exited unexpectedly with code: {exitCode}", ErrorCode.ProcessError)); // Use helper
        await UpdateStatusAsync(ProcessStatus.Crashed);
        // L.LogError("Process crashed: {0} (exit code: {1})", Id, exitCode);
      }
      else
      {
        // If it exited cleanly but wasn't explicitly stopped, record it.
        StopTime = DateTime.UtcNow; // Record exit time
        await UpdateStatusAsync(ProcessStatus.Stopped);
        // L.LogInfo("Process exited normally (externally?): {0}", Id);
      }

      // Clean up the process object as it has exited
      _process?.Dispose();
      _process = null;
    }
    finally
    {
      _lock.Release();
    }
  }


  private async Task UpdateStatusAsync(ProcessStatus newStatus)
  {
    var oldStatus = Status;
    // Avoid redundant updates
    if (oldStatus == newStatus) return;

    Status = newStatus;

    try
    {
      // Use Task.Run to invoke event handlers off the async context if needed,
      // or ensure handlers are non-blocking. Direct invoke is often fine.
      StatusChanged?.Invoke(this, new ProcessStatusEventArgs(Id, oldStatus, newStatus, DateTime.UtcNow));
    }
    catch (Exception ex)
    {
      // L.LogError("Error in status change handler for {0}: {1}", Id, ex.Message);
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_isDisposed) return;

    // Use a timeout for the lock wait during disposal to prevent deadlocks
    bool lockAcquired = await _lock.WaitAsync(TimeSpan.FromSeconds(5));
    if (!lockAcquired)
    {
      // L.LogWarn("Timeout acquiring lock during disposal for process {0}. Forcing disposal.", Id);
      // Proceed with disposal carefully without the lock, potential for race conditions
    }

    try
    {
      if (_isDisposed) return; // Double-check after acquiring lock (if acquired)
      _isDisposed = true;

      // Unsubscribe event handlers to prevent issues after disposal
      if (_process != null)
      {
        try
        {
          // Check if process object itself is disposed before accessing properties
          if (!_process.HasExited) // This might throw if process is disposed
          {
            _process.EnableRaisingEvents = false; // Stop listening
            _process.OutputDataReceived -= HandleOutputData;
            _process.ErrorDataReceived -= HandleErrorData;
            _process.Exited -= HandleProcessExited;

            // Attempt to kill if still running
            try { _process.Kill(entireProcessTree: true); } catch {/* Ignore */}
          }
        }
        catch (Exception ex) { /* L.LogError("Error detaching events or killing process during disposal: {0}", ex, Id); */ }
        finally
        {
          _process.Dispose(); // Dispose the Process object
          _process = null;
        }
      }

      // Dispose the semaphore if lock was acquired
      if(lockAcquired) _lock.Dispose();

      _outputBuffer.Clear();
      _errorBuffer.Clear();
      // L.LogDebug("Disposed ProcessInfo: {0}", Id);
    }
    finally
    {
      // Only release if lock was acquired and disposal hasn't happened yet
      if (lockAcquired && !_isDisposed)
      {
        _lock.Release();
      }
      else if (lockAcquired && _isDisposed)
      {
        // If disposed within the lock, ensure it's not released again.
        // The lock itself is disposed now.
      }
    }
  }

  public async Task WaitForExitAsync()
  {
    Process? p = _process; // Capture reference
    if (p == null || _isDisposed)
    {
      // If process never started, isn't running, or is disposed, return immediately or throw
      if (Status == ProcessStatus.Stopped || Status == ProcessStatus.Failed || Status == ProcessStatus.Crashed) return;
      throw new InvalidOperationException("Process not started or has been disposed.");
    }
    await p.WaitForExitAsync(); // Use async version
  }

  // Add methods to access buffers if needed
  public List<string> GetOutputBufferSnapshot()
  {
    lock(_outputBuffer) { return new List<string>(_outputBuffer); }
  }
  public List<string> GetErrorBufferSnapshot()
  {
    lock(_errorBuffer) { return new List<string>(_errorBuffer); }
  }
  public ProcessState GetProcessState()
  {
    return new ProcessState
    {
      Id = Id,
      Name = Metadata.Name,
      IsRunning = IsRunning,
      IsService = Metadata.Type == "service",
      StartTime = StartTime,
      EndTime = StopTime,
      LastMetrics = null, // Placeholder for metrics if needed
      LastSeen = DateTime.UtcNow
    };
  }
}


[MemoryPackable]
public partial record ProcessMetadata(
    string Name,
    string Type,
    string Version,
    Dictionary<string, string> Environment,
    Dictionary<string, string> Configuration
);
