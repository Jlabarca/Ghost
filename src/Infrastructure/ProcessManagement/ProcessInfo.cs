using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Ghost.Infrastructure.ProcessManagement;

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
    Dictionary<string, string> Configuration);

public class ProcessInfo : IAsyncDisposable
{
    // Basic Properties
    public string Id { get; }
    public ProcessMetadata Metadata { get; }
    public ProcessStatus Status { get; private set; }
    public DateTime StartTime { get; private set; }
    public DateTime? StopTime { get; private set; }
    public int RestartCount { get; private set; }
    public Exception? LastError { get; private set; }

    // Runtime Properties
    [JsonIgnore]
    public bool IsRunning => Status == ProcessStatus.Running;

    [JsonIgnore]
    public TimeSpan Uptime => StopTime.HasValue
        ? StopTime.Value - StartTime
        : DateTime.UtcNow - StartTime;

    // Private fields
    private Process? _process;
    private readonly ProcessStartInfo _startInfo;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<string> _outputBuffer = new();
    private readonly List<string> _errorBuffer = new();
    private const int MaxBufferSize = 1000;
    private bool _isDisposed;

    // Events
    public event EventHandler<ProcessOutputEventArgs>? OutputReceived;
    public event EventHandler<ProcessOutputEventArgs>? ErrorReceived;
    public event EventHandler<ProcessStatusEventArgs>? StatusChanged;

    public ProcessInfo(string id, ProcessMetadata metadata, ProcessStartInfo startInfo)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _startInfo = startInfo ?? throw new ArgumentNullException(nameof(startInfo));
        Status = ProcessStatus.Stopped;

        // Configure process startup settings
        _startInfo.RedirectStandardOutput = true;
        _startInfo.RedirectStandardError = true;
        _startInfo.UseShellExecute = false;
        _startInfo.CreateNoWindow = true;

        // Add environment variables
        foreach (var (key, value) in metadata.Environment)
        {
            _startInfo.EnvironmentVariables[key] = value;
        }
    }

    public async Task StartAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ProcessInfo));

            if (Status == ProcessStatus.Running)
                throw new InvalidOperationException("Process is already running");

            await UpdateStatusAsync(ProcessStatus.Starting);

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
        }
        catch (Exception ex)
        {
            LastError = ex;
            await UpdateStatusAsync(ProcessStatus.Failed);
            throw;
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
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ProcessInfo));

            if (_process == null || Status == ProcessStatus.Stopped)
                return;

            await UpdateStatusAsync(ProcessStatus.Stopping);

            try
            {
                // Try graceful shutdown first
                if (!_process.HasExited)
                {
                    _process.CloseMainWindow();

                    if (!_process.WaitForExit((int)timeout.TotalMilliseconds))
                    {
                        // Force kill if necessary
                        _process.Kill(entireProcessTree: true);
                    }
                }

                StopTime = DateTime.UtcNow;
                await UpdateStatusAsync(ProcessStatus.Stopped);
            }
            catch (Exception ex)
            {
                LastError = ex;
                await UpdateStatusAsync(ProcessStatus.Failed);
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
    }

    public async Task<Dictionary<string, string>> GetProcessInfoAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return new Dictionary<string, string>
            {
                ["id"] = Id,
                ["name"] = Metadata.Name,
                ["type"] = Metadata.Type,
                ["version"] = Metadata.Version,
                ["status"] = Status.ToString(),
                ["startTime"] = StartTime.ToString("o"),
                ["stopTime"] = StopTime?.ToString("o") ?? "",
                ["uptime"] = Uptime.ToString(),
                ["restartCount"] = RestartCount.ToString(),
                ["error"] = LastError?.Message ?? ""
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public IEnumerable<string> GetRecentOutput()
    {
        lock (_outputBuffer)
        {
            return _outputBuffer.ToList();
        }
    }

    public IEnumerable<string> GetRecentErrors()
    {
        lock (_errorBuffer)
        {
            return _errorBuffer.ToList();
        }
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
                    if (_outputBuffer.Count > MaxBufferSize)
                        _outputBuffer.RemoveAt(0);
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
                    if (_errorBuffer.Count > MaxBufferSize)
                        _errorBuffer.RemoveAt(0);
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
            }
            else
            {
                await UpdateStatusAsync(ProcessStatus.Stopped);
            }
        };
    }

    private async Task UpdateStatusAsync(ProcessStatus newStatus)
    {
        var oldStatus = Status;
        Status = newStatus;

        StatusChanged?.Invoke(this, new ProcessStatusEventArgs(
            Id, oldStatus, newStatus, DateTime.UtcNow));
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
                catch
                {
                    // Best effort cleanup
                }
                finally
                {
                    _process.Dispose();
                }
            }

            _lock.Dispose();
            _outputBuffer.Clear();
            _errorBuffer.Clear();
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