using Ghost.Core.Exceptions;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Ghost.Core.Monitoring;

public enum ProcessStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
    Crashed,
    Warning
}

public record ProcessMetadata(
    string Name,
    string Type,
    string Version,
    Dictionary<string, string> Environment,
    Dictionary<string, string> Configuration
);

public class ProcessInfo : IAsyncDisposable
{
    // Required parameterless constructor for Dapper
    public ProcessInfo()
    {
        _startInfo = new ProcessStartInfo();
        _lock = new SemaphoreSlim(1, 1);
        _outputBuffer = new List<string>();
        _errorBuffer = new List<string>();
        _maxBufferSize = 1000;
    }

    public ProcessInfo(
        string id,
        ProcessMetadata metadata,
        string executablePath,
        string arguments,
        string workingDirectory,
        Dictionary<string, string> environment,
        int maxBufferSize = 1000)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        ExecutablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        Arguments = arguments ?? string.Empty;
        WorkingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
        _maxBufferSize = maxBufferSize;

        _startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Add environment variables
        foreach (var (key, value) in environment)
        {
            _startInfo.EnvironmentVariables[key] = value;
        }

        foreach (var (key, value) in metadata.Environment)
        {
            _startInfo.EnvironmentVariables[key] = value;
        }

        _lock = new SemaphoreSlim(1, 1);
        _outputBuffer = new List<string>();
        _errorBuffer = new List<string>();

        Status = ProcessStatus.Stopped;
        G.LogDebug("Created ProcessInfo: {0} ({1})", Id, metadata.Name);
    }

    // Identity
    public string Id { get; set; } = string.Empty;
    public ProcessMetadata Metadata { get; set; }

    // Process Info
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;

    // Status
    public ProcessStatus Status { get; private set; }
    public DateTime StartTime { get; private set; }
    public DateTime? StopTime { get; private set; }
    public int RestartCount { get; private set; }
    public Exception? LastError { get; private set; }

    // Runtime Properties
    [JsonIgnore]
    public bool IsRunning => Status == ProcessStatus.Running;

    [JsonIgnore]
    public TimeSpan Uptime => StopTime.HasValue ? StopTime.Value - StartTime : DateTime.UtcNow - StartTime;

    // Private fields
    private Process? _process;
    private readonly ProcessStartInfo _startInfo;
    private readonly SemaphoreSlim _lock;
    private readonly List<string> _outputBuffer;
    private readonly List<string> _errorBuffer;
    private readonly int _maxBufferSize;
    private bool _isDisposed;

    // Events
    public event EventHandler<ProcessOutputEventArgs>? OutputReceived;
    public event EventHandler<ProcessOutputEventArgs>? ErrorReceived;
    public event EventHandler<ProcessStatusEventArgs>? StatusChanged;

    // Rest of the implementation remains the same...
    // Copy over all the existing methods from the original ProcessInfo.cs

    public async Task StartAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(ProcessInfo));
            if (Status == ProcessStatus.Running)
            {
                G.LogWarn("Process already running: {0}", Id);
                return;
            }

            await UpdateStatusAsync(ProcessStatus.Starting);
            try
            {
                _process = new Process { StartInfo = _startInfo };
                ConfigureProcessCallbacks();
                if (!_process.Start())
                {
                    throw new GhostException(
                        $"Failed to start process {Id}",
                        ErrorCode.ProcessStartFailed);
                }

                StartTime = DateTime.UtcNow;
                await UpdateStatusAsync(ProcessStatus.Running);

                // Start reading output/error asynchronously
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                G.LogInfo("Started process: {0}", Id);
            }
            catch (Exception ex)
            {
                LastError = ex;
                await UpdateStatusAsync(ProcessStatus.Failed);
                G.LogError("Failed to start process: {0}", ex, Id);
                throw;
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
            if (_process == null || Status == ProcessStatus.Stopped)
            {
                return;
            }

            await UpdateStatusAsync(ProcessStatus.Stopping);
            try
            {
                if (!_process.HasExited)
                {
                    _process.CloseMainWindow();
                    if (!_process.WaitForExit((int)timeout.TotalMilliseconds))
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }

                StopTime = DateTime.UtcNow;
                await UpdateStatusAsync(ProcessStatus.Stopped);
                G.LogInfo("Stopped process: {0}", Id);
            }
            catch (Exception ex)
            {
                LastError = ex;
                await UpdateStatusAsync(ProcessStatus.Failed);
                G.LogError("Failed to stop process: {0}", ex, Id);
                throw new GhostException(
                    $"Failed to stop process {Id}",
                    ex,
                    ErrorCode.ProcessError);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RestartAsync(TimeSpan timeout)
    {
        await StopAsync(timeout);
        await StartAsync();
        RestartCount++;
        G.LogInfo("Restarted process: {0} (restart count: {1})", Id, RestartCount);
    }

    private void ConfigureProcessCallbacks()
    {
        if (_process == null) return;

        _process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (_outputBuffer)
                {
                    _outputBuffer.Add(e.Data);
                    while (_outputBuffer.Count > _maxBufferSize)
                    {
                        _outputBuffer.RemoveAt(0);
                    }
                }
                OutputReceived?.Invoke(this, new ProcessOutputEventArgs(e.Data));
            }
        };

        _process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (_errorBuffer)
                {
                    _errorBuffer.Add(e.Data);
                    while (_errorBuffer.Count > _maxBufferSize)
                    {
                        _errorBuffer.RemoveAt(0);
                    }
                }
                ErrorReceived?.Invoke(this, new ProcessOutputEventArgs(e.Data));
            }
        };

        _process.Exited += async (s, e) =>
        {
            if (_process.ExitCode != 0)
            {
                LastError = new GhostException(
                    $"Process exited with code: {_process.ExitCode}",
                    ErrorCode.ProcessError);
                await UpdateStatusAsync(ProcessStatus.Crashed);
                G.LogError("Process crashed: {0} (exit code: {1})", Id, _process.ExitCode);
            }
            else
            {
                await UpdateStatusAsync(ProcessStatus.Stopped);
                G.LogInfo("Process exited normally: {0}", Id);
            }
        };
    }

    private async Task UpdateStatusAsync(ProcessStatus newStatus)
    {
        var oldStatus = Status;
        Status = newStatus;

        try
        {
            StatusChanged?.Invoke(this, new ProcessStatusEventArgs(
                Id, oldStatus, newStatus, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            G.LogError("Error in status change handler: {0}", ex, Id);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        await _lock.WaitAsync();
        try
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)
                {
                    G.LogError("Error killing process during disposal: {0}", ex, Id);
                }
                finally
                {
                    _process.Dispose();
                }
            }

            _lock.Dispose();
            _outputBuffer.Clear();
            _errorBuffer.Clear();
            G.LogDebug("Disposed ProcessInfo: {0}", Id);
        }
        finally
        {
            _lock.Release();
        }
    }
}

public class ProcessOutputEventArgs : EventArgs
{
    public string Data { get; }
    public DateTime Timestamp { get; }

    public ProcessOutputEventArgs(string data)
    {
        Data = data;
        Timestamp = DateTime.UtcNow;
    }
}

public class ProcessStatusEventArgs : EventArgs
{
    public string ProcessId { get; }
    public ProcessStatus OldStatus { get; }
    public ProcessStatus NewStatus { get; }
    public DateTime Timestamp { get; }

    public ProcessStatusEventArgs(
        string processId,
        ProcessStatus oldStatus,
        ProcessStatus newStatus,
        DateTime timestamp)
    {
        ProcessId = processId;
        OldStatus = oldStatus;
        NewStatus = newStatus;
        Timestamp = timestamp;
    }
}