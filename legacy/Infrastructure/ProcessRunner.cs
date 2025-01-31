using System.Diagnostics;
using System.Text;
using Ghost.Legacy.Infrastructure;
using Spectre.Console;

namespace Ghost.Legacy.Services
{
    public class ProcessRunner
    {
        private readonly GhostLogger _logger;
        private readonly bool _debug;

        public ProcessRunner(GhostLogger logger, bool debug = false)
        {
            _logger = logger;
            _debug = debug;
        }

        /// <summary>
        /// Runs a process asynchronously with full control over output handling and cancellation.
        /// Think of this as a smart controller that can manage and monitor any process.
        /// </summary>
        public async Task<ProcessResult> RunProcessAsync(
            string command,
            string[] args,
            string workingDirectory = null,
            Action<string> outputCallback = null,
            Action<string> errorCallback = null,
            CancellationToken cancellationToken = default)
        {
            // Build the process configuration - like setting up automation rules
            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false, // We're not handling input in this version
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            // Log the command if in debug mode
            LogDebug($"Executing: {command} {string.Join(" ", args)}");
            LogDebug($"Working directory: {startInfo.WorkingDirectory}");

            // Set up our output collection system - like installing sensors
            using var process = new Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            // Wire up our output handlers - like connecting monitoring devices
            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    outputBuilder.AppendLine(e.Data);
                    outputCallback?.Invoke(e.Data);
                    LogDebug($"Output: {e.Data}");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    errorBuilder.AppendLine(e.Data);
                    errorCallback?.Invoke(e.Data);
                    LogDebug($"Error: {e.Data}");
                }
            };

            try
            {
                // Start the process - like turning on the system
                var processStarted = process.Start();
                if (!processStarted)
                {
                    throw new GhostException(
                        $"Failed to start process: {command}",
                        ErrorCode.ProcessError);
                }

                // Begin capturing output - like starting to record
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Set up cancellation handler - like wiring up the emergency stop
                using var cancellationRegistration = cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            LogDebug("Cancellation requested - stopping process");
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"Failed to kill process during cancellation: {ex.Message}");
                    }
                });

                try
                {
                    // Wait for process completion or cancellation - like monitoring until task completion
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    LogDebug("Process was cancelled");
                    throw;
                }

                // Package up the results - like preparing the final report
                var result = new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = outputBuilder.ToString().TrimEnd(),
                    StandardError = errorBuilder.ToString().TrimEnd()
                };

                LogDebug($"Process completed with exit code: {result.ExitCode}");
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Handle unexpected errors - like system malfunction alerts
                LogDebug($"Process execution failed: {ex.Message}");
                throw new GhostException(
                    $"Failed to execute command '{command}': {ex.Message}",
                    ErrorCode.ProcessError);
            }
        }

        // Helper method for running simpler processes
        public async Task<ProcessResult> RunQuickProcessAsync(
            string command,
            string[] args,
            string workingDirectory = null)
        {
            return await RunProcessAsync(
                command,
                args,
                workingDirectory,
                outputCallback: null,
                errorCallback: null);
        }

        /// <summary>
        /// Runs a process synchronously and captures its output.
        /// </summary>
        public ProcessResult RunProcess(string command, string[] args, string workingDirectory = null)
        {
            var fullCommand = $"{command} {string.Join(" ", args)}";
            LogDebug($"Executing command: {fullCommand}");
            LogDebug($"Working directory: {workingDirectory ?? Environment.CurrentDirectory}");

            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                    LogDebug($"Output: {e.Data}");
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    error.AppendLine(e.Data);
                    LogDebug($"Error: {e.Data}");
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                var result = new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = output.ToString().TrimEnd(),
                    StandardError = error.ToString().TrimEnd()
                };

                LogDebug($"Process exited with code: {result.ExitCode}");
                return result;
            }
            catch (Exception ex)
            {
                LogDebug($"Process execution failed: {ex.Message}");
                throw new GhostException(
                    $"Failed to execute command '{command}': {ex.Message}",
                    ErrorCode.ProcessError);
            }
        }

        private void LogDebug(string message)
        {
            if (_debug)
            {
                _logger.Log("ProcessRunner", message);
                AnsiConsole.MarkupLine($"[grey]DEBUG: {message.EscapeMarkup()}[/]");
            }
        }
    }

    public class ProcessResult
    {
        public int ExitCode { get; init; }
        public string StandardOutput { get; init; }
        public string StandardError { get; init; }
        public bool Success => ExitCode == 0;

        public void EnsureSuccessfulExit()
        {
            if (!Success)
            {
                var error = string.IsNullOrEmpty(StandardError)
                    ? StandardOutput
                    : StandardError;

                throw new GhostException(
                    $"Process failed with exit code {ExitCode}: {error}",
                    ErrorCode.ProcessError);
            }
        }
    }
}