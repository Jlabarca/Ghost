using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using Spectre.Console;

namespace Ghost.Infrastructure
{
    public class ProcessRunner
    {
        public ProcessRunner()
        {
        }
        // Run a process synchronously and capture output
        public ProcessResult RunProcess(string command, string[] args, string workingDirectory = null)
        {
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
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    error.AppendLine(e.Data);
                }
            };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = output.ToString().TrimEnd(),
                    StandardError = error.ToString().TrimEnd()
                };
            }
            catch (Exception ex)
            {
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
                    outputCallback?.Invoke(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    error.AppendLine(e.Data);
                    errorCallback?.Invoke(e.Data);
                }
            };

            try
            {
                var processStarted = process.Start();
                if (!processStarted)
                {
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
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Best effort to kill the process
                    }
                });

                await process.WaitForExitAsync(cancellationToken);

                return new ProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = output.ToString().TrimEnd(),
                    StandardError = error.ToString().TrimEnd()
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
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
            foreach (var variable in GetInheritedEnvironmentVariables())
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }

            try
            {
                using var process = new Process { StartInfo = startInfo };
                
                process.Start();
                await process.WaitForExitAsync();
                
                return process.ExitCode;
            }
            catch (Exception ex)
            {
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