using System.Diagnostics;

namespace Ghost2.Infrastructure.ProcessManagement;

public enum ProcessStatus
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
    Crashed
}

public record ProcessMetadata(
    string Name,
    string Type,
    string Version,
    Dictionary<string, string> Environment,
    Dictionary<string, string> Configuration);

public class ProcessInfo : IAsyncDisposable
{
    public string Id { get; }
    public ProcessMetadata Metadata { get; }
    public ProcessStatus Status { get; private set; }
    public DateTime StartTime { get; private set; }
    public DateTime? StopTime { get; private set; }
    public int RestartCount { get; private set; }
    public Exception? LastError { get; private set; }

    private Process? _process;
    private readonly ProcessStartInfo _startInfo;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ProcessMetricsCollector _metricsCollector;
    private bool _isDisposed;

    public ProcessInfo(string id, ProcessMetadata metadata, ProcessStartInfo startInfo)
    {
        Id = id;
        Metadata = metadata;
        _startInfo = startInfo;
        Status = ProcessStatus.Stopped;
        _metricsCollector = new ProcessMetricsCollector();
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

            Status = ProcessStatus.Starting;
            _process = new Process { StartInfo = _startInfo };

            // Setup output handling
            _process.OutputDataReceived += (s, e) => OnOutputReceived(e.Data);
            _process.ErrorDataReceived += (s, e) => OnErrorReceived(e.Data);

            // Start process
            if (!_process.Start())
            {
                throw new GhostException(
                    $"Failed to start process {Id}",
                    ErrorCode.ProcessStartFailed);
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            StartTime = DateTime.UtcNow;
            Status = ProcessStatus.Running;

            // Start metrics collection
            await _metricsCollector.StartAsync(_process);
        }
        catch (Exception ex)
        {
            Status = ProcessStatus.Failed;
            LastError = ex;
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

            Status = ProcessStatus.Stopping;

            // Stop metrics collection
            await _metricsCollector.StopAsync();

            // Try graceful shutdown first
            _process.CloseMainWindow();
            if (!_process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                // Force kill if necessary
                _process.Kill(true);
            }

            StopTime = DateTime.UtcNow;
            Status = ProcessStatus.Stopped;
        }
        catch (Exception ex)
        {
            Status = ProcessStatus.Failed;
            LastError = ex;
            throw;
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

    private void OnOutputReceived(string? data)
    {
        if (data != null)
        {
            // Handle process output
            // This could be forwarded to a logging system or handled by event handlers
            Console.WriteLine($"[{Id}] {data}");
        }
    }

    private void OnErrorReceived(string? data)
    {
        if (data != null)
        {
            // Handle process errors
            // This could be forwarded to a logging system or handled by event handlers
            Console.Error.WriteLine($"[{Id}] ERROR: {data}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        await _lock.WaitAsync();
        try
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            if (_process != null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(true);
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

            await _metricsCollector.DisposeAsync();
            _lock.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }
}
internal class ProcessMetricsCollector
{
    public async Task StartAsync(Process process)
    {
        throw new NotImplementedException();
    }
    public async Task DisposeAsync()
    {
        throw new NotImplementedException();
    }
    public async Task StopAsync()
    {
        throw new NotImplementedException();
    }
}
