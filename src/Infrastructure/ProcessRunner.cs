using System.Diagnostics;
using System.Text;
using Spectre.Console;

namespace Ghost.Infrastructure
{
    public class ProcessRunner
    {
        public class ProcessOutputEventArgs : EventArgs
        {
            public string Data { get; }
            public bool IsError { get; }
            public ProcessOutputEventArgs(string data, bool isError)
            {
                Data = data;
                IsError = isError;
            }
        }

        public event EventHandler<ProcessOutputEventArgs> OutputReceived;

        private readonly GhostLogger _logger;
        private readonly bool _debug;

        public ProcessRunner(GhostLogger logger, bool debug = false)
        {
            _logger = logger;
            _debug = debug;
        }

        private void OnOutputReceived(string data, bool isError)
        {
            OutputReceived?.Invoke(this, new ProcessOutputEventArgs(data, isError));
        }

        public async Task<int> RunWithArgsAsync(
            string command,
            string[] appArgs,
            string[] passthroughArgs,
            string workDir,
            string instanceId)
        {
            var allArgs = new List<string>(appArgs);
            if (passthroughArgs?.Length > 0)
            {
                allArgs.AddRange(passthroughArgs);
            }

            var fullCommand = $"{command} {string.Join(" ", allArgs)}";
            _logger.Log(instanceId, $"Executing command: {fullCommand}");
            _logger.Log(instanceId, $"Working directory: {workDir}");

            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = string.Join(" ", allArgs),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };

            // Set environment variables
            foreach (var variable in GetInheritedEnvironmentVariables())
            {
                startInfo.Environment[variable.Key] = variable.Value;
                LogDebug($"Setting environment variable: {variable.Key}={variable.Value}");
            }

            try
            {
                using var process = new Process { StartInfo = startInfo };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        Console.WriteLine(e.Data);
                        _logger.Log(instanceId, $"[OUT] {e.Data}");
                        OnOutputReceived(e.Data, false);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        Console.Error.WriteLine(e.Data);
                        _logger.Log(instanceId, $"[ERR] {e.Data}");
                        OnOutputReceived(e.Data, true);
                    }
                };

                process.Start();
                _logger.Log(instanceId, "Process started");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Handle standard input
                var inputTask = Task.Run(() =>
                {
                    try
                    {
                        while (!process.HasExited)
                        {
                            var input = Console.ReadLine();
                            if (input != null)
                            {
                                process.StandardInput.WriteLine(input);
                                _logger.Log(instanceId, $"[IN] {input}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Log(instanceId, $"Input handling error: {ex.Message}");
                    }
                });

                await process.WaitForExitAsync();
                var exitCode = process.ExitCode;
                _logger.Log(instanceId, $"Process exited with code: {exitCode}");
                return exitCode;
            }
            catch (Exception ex)
            {
                _logger.Log(instanceId, $"Process execution failed: {ex.Message}");
                throw new GhostException(
                    $"Failed to execute command '{command}': {ex.Message}",
                    ErrorCode.ProcessError);
            }
        }

        private void LogDebug(string message)
        {
            if (_debug)
            {
                AnsiConsole.MarkupLine($"[grey]DEBUG: {message.EscapeMarkup()}[/]");
            }
        }

        // Run a process synchronously and capture output
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

        // Run a process asynchronously and capture output
        public async Task<ProcessResult> RunProcessAsync(
            string command,
            string[] args,
            string workingDirectory = null,
            Action<string> outputCallback = null,
            Action<string> errorCallback = null,
            CancellationToken cancellationToken = default)
        {
            var fullCommand = $"{command} {string.Join(" ", args)}";
            LogDebug($"Executing async command: {fullCommand}");
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
                    outputCallback?.Invoke(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    error.AppendLine(e.Data);
                    LogDebug($"Error: {e.Data}");
                    errorCallback?.Invoke(e.Data);
                }
            };

            try
            {
                var processStarted = process.Start();
                if (!processStarted)
                {
                    LogDebug($"Failed to start process: {command}");
                    throw new GhostException(
                        $"Failed to start process '{command}'",
                        ErrorCode.ProcessError);
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Register cancellation
                cancellationToken.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            LogDebug("Cancellation requested - killing process");
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        LogDebug("Failed to kill process during cancellation");
                    }
                });

                await process.WaitForExitAsync(cancellationToken);

                var result = new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = output.ToString().TrimEnd(),
                    StandardError = error.ToString().TrimEnd()
                };

                LogDebug($"Async process exited with code: {result.ExitCode}");
                return result;
            }
            catch (OperationCanceledException)
            {
                LogDebug("Process was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                LogDebug($"Async process execution failed: {ex.Message}");
                throw new GhostException(
                    $"Failed to execute command '{command}': {ex.Message}",
                    ErrorCode.ProcessError);
            }
        }

        // Run a process with passthrough to console (for interactive processes)
        public async Task<int> RunWithArgsAsync(
            string command,
            string[] appArgs,
            string[] passthroughArgs,
            string workDir)
        {
            var allArgs = new List<string>(appArgs);
            if (passthroughArgs?.Length > 0)
            {
                allArgs.Add("--");
                allArgs.AddRange(passthroughArgs);
            }

            var fullCommand = $"{command} {string.Join(" ", allArgs)}";
            LogDebug($"Executing interactive command: {fullCommand}");
            LogDebug($"Working directory: {workDir}");

            var startInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = string.Join(" ", allArgs),
                RedirectStandardOutput = false, // Let output go directly to console
                RedirectStandardError = false,  // Let error go directly to console
                RedirectStandardInput = false,  // Allow interactive input
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };

            // Set environment variables
            var envVars = GetInheritedEnvironmentVariables();
            foreach (var variable in envVars)
            {
                startInfo.Environment[variable.Key] = variable.Value;
                LogDebug($"Setting environment variable: {variable.Key}={variable.Value}");
            }

            try
            {
                using var process = new Process { StartInfo = startInfo };
                process.Start();

                LogDebug("Interactive process started");
                await process.WaitForExitAsync();

                var exitCode = process.ExitCode;
                LogDebug($"Interactive process exited with code: {exitCode}");

                return exitCode;
            }
            catch (Exception ex)
            {
                LogDebug($"Interactive process execution failed: {ex.Message}");
                throw new GhostException(
                    $"Failed to execute command '{command}': {ex.Message}",
                    ErrorCode.ProcessError);
            }
        }

        // Helper to get important environment variables to pass through
        private Dictionary<string, string> GetInheritedEnvironmentVariables()
        {
            var variables = new Dictionary<string, string>();
            var inheritedVars = new[]
            {
                "PATH",
                "DOTNET_ROOT",
                "DOTNET_MULTILEVEL_LOOKUP",
                "ASPNETCORE_ENVIRONMENT",
                "DOTNET_CLI_TELEMETRY_OPTOUT",
                // Add other variables as needed
            };

            foreach (var name in inheritedVars)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrEmpty(value))
                {
                    variables[name] = value;
                }
            }

            return variables;
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