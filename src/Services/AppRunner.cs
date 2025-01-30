using Ghost.Infrastructure;
using Spectre.Console;

namespace Ghost.Services
{
    public class AppRunner
    {
        private readonly ProcessRunner _processRunner;
        private readonly GhostLogger _logger;
        private readonly string _workspacePath;
        private readonly bool _debug;

        public AppRunner(ProcessRunner processRunner, GhostLogger logger, ConfigManager configManager, bool debug = false)
        {
            _processRunner = processRunner;
            _logger = logger;
            _debug = debug;

            // Get workspace path from config or use default
            var settings = configManager.GetWorkspaceSettings();
            _workspacePath = settings?.Path ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ghost",
                "workspace");
        }

        public async Task<int> RunAsync(string url, string[] args, string instanceId, CancellationToken cancellationToken = default)
        {
            // Create a unique working directory
            var projectName = SanitizeProjectName(new Uri(url).Segments.Last().TrimEnd('/'));
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var workDir = Path.Combine(_workspacePath, $"{projectName}_{timestamp}_{instanceId}");

            _logger.Log(instanceId, $"Creating workspace directory: {workDir}");
            Directory.CreateDirectory(workDir);

            try
            {
                // Clone repository with progress reporting
                await AnsiConsole.Progress()
                    .StartAsync(async ctx =>
                    {
                        var cloneTask = ctx.AddTask("[green]Cloning repository[/]");
                        cloneTask.MaxValue = 100;

                        var cloneResult = await _processRunner.RunProcessAsync(
                            "git",
                            new[] { "clone", url, workDir },
                            workDir,
                            outputCallback: line => {
                                _logger.Log(instanceId, $"[Clone] {line}");
                                cloneTask.Increment(5); // Approximate progress
                            },
                            errorCallback: line => {
                                _logger.Log(instanceId, $"[Clone Error] {line}");
                            },
                            cancellationToken);

                        if (!cloneResult.Success)
                        {
                            throw new GhostException(
                                $"Failed to clone repository: {cloneResult.StandardError}",
                                ErrorCode.ProcessError);
                        }

                        cloneTask.Value = 100;
                    });

                // Build project with progress reporting
                await AnsiConsole.Progress()
                    .StartAsync(async ctx =>
                    {
                        var buildTask = ctx.AddTask("[green]Building project[/]");
                        buildTask.MaxValue = 100;

                        var buildResult = await _processRunner.RunProcessAsync(
                            "dotnet",
                            new[] { "build", workDir },
                            workDir,
                            outputCallback: line => {
                                _logger.Log(instanceId, $"[Build] {line}");
                                buildTask.Increment(10); // Approximate progress
                            },
                            errorCallback: line => {
                                _logger.Log(instanceId, $"[Build Error] {line}");
                            },
                            cancellationToken);

                        if (!buildResult.Success)
                        {
                            throw new GhostException(
                                $"Failed to build project: {buildResult.StandardError}",
                                ErrorCode.BuildFailed);
                        }

                        buildTask.Value = 100;
                    });

                // Prepare the run command
                var runArgs = new List<string> { "run", "--project", workDir };
                if (args?.Length > 0)
                {
                    runArgs.Add("--"); // Separator for user arguments
                    runArgs.AddRange(args);
                }

                _logger.Log(instanceId, $"Running project with args: {string.Join(" ", runArgs)}");

                // Run the project with interactive output
                var result = await _processRunner.RunProcessAsync(
                    "dotnet",
                    runArgs.ToArray(),
                    workDir,
                    outputCallback: line => {
                        Console.WriteLine(line);
                        _logger.Log(instanceId, $"[Run] {line}");
                    },
                    errorCallback: line => {
                        Console.Error.WriteLine(line);
                        _logger.Log(instanceId, $"[Run Error] {line}");
                    },
                    cancellationToken);

                return result.ExitCode;
            }
            catch (OperationCanceledException)
            {
                _logger.Log(instanceId, "Operation was cancelled by user");
                AnsiConsole.MarkupLine("[yellow]Operation cancelled by user[/]");
                return 1;
            }
            catch (Exception ex)
            {
                _logger.Log(instanceId, $"Error running application: {ex}");
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                return 1;
            }
            finally
            {
                try
                {
                    if (_debug)
                    {
                        _logger.Log(instanceId, "Skipping workspace cleanup due to debug mode");
                        AnsiConsole.MarkupLine($"[grey]Debug: Workspace preserved at {workDir}[/]");
                    }
                    else
                    {
                        _logger.Log(instanceId, "Cleaning up workspace");
                        AnsiConsole.MarkupLine("[grey]Cleaning up workspace...[/]");
                        Directory.Delete(workDir, recursive: true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log(instanceId, $"Workspace cleanup failed: {ex}");
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] Workspace cleanup incomplete: {ex.Message}");
                    AnsiConsole.MarkupLine($"[grey]You may need to manually delete: {workDir}[/]");
                }
            }
        }

        private string SanitizeProjectName(string name)
        {
            // Remove invalid characters from project name
            var invalidChars = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
        }
    }
}