using Ghost.Infrastructure;
using Spectre.Console;

namespace Ghost.Services
{
    public class AppRunner
    {
        private readonly ProcessRunner _processRunner;
        private readonly string _workspacePath;

        public AppRunner(ProcessRunner processRunner)
        {
            _processRunner = processRunner;
            _workspacePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ghost",
                "workspace");
        }

        public async Task<int> RunAsync(string url, string[] args, string instanceId)
        {
            var projectName = new Uri(url).Segments.Last().TrimEnd('/');
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var workDir = Path.Combine(_workspacePath, $"{projectName}_{timestamp}");

            AnsiConsole.MarkupLine($"[grey]Creating workspace directory: {workDir}[/]");
            Directory.CreateDirectory(workDir);

            try
            {
                AnsiConsole.Status().Start("Cloning repository...", ctx =>
                {
                    var result = _processRunner.RunProcess("git", new[] { "clone", url, workDir });
                    if (result.ExitCode != 0)
                    {
                        throw new GhostException($"Failed to clone repository: {result.StandardError}");
                    }
                });

                AnsiConsole.Status().Start("Building project...", ctx =>
                {
                    var result = _processRunner.RunProcess("dotnet", new[] { "build", workDir });
                    if (result.ExitCode != 0)
                    {
                        throw new GhostException($"Failed to build project: {result.StandardError}");
                    }
                });

                // Construct the final command with all arguments
                var dotnetArgs = new List<string> { "run", "--project", workDir };
                if (args?.Length > 0)
                {
                    dotnetArgs.Add("--");  // Add separator before user arguments
                    dotnetArgs.AddRange(args);
                }

                var passArgs = new List<string> { "run", "--project", workDir };
                return await _processRunner.RunWithArgsAsync("dotnet", dotnetArgs.ToArray(), passArgs.ToArray(), workDir, instanceId);
            }
            finally
            {
                try
                {
                    AnsiConsole.MarkupLine("[grey]Cleaning up workspace...[/]");
                    Directory.Delete(workDir, recursive: true);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Warning:[/] Workspace cleanup incomplete: {ex.Message}");
                    AnsiConsole.MarkupLine($"[grey]You may need to manually delete: {workDir}[/]");
                }
            }
        }
    }
}