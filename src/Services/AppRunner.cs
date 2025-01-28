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
                "Ghost", "workspace");
        }

        public async Task<int> RunAsync(string url, string[] args)
        {
            // Create workspace directory
            var workDir = Path.Combine(_workspacePath, Path.GetRandomFileName());
            Directory.CreateDirectory(workDir);

            try
            {
                // Clone repository
                AnsiConsole.Status()
                    .Start("Cloning repository...", ctx =>
                    {
                        var result = _processRunner.RunProcess("git", new[] { "clone", url, workDir });
                        if (result != null)
                        {
                            throw new GhostException("Failed to clone repository");
                        }
                    });

                // Build project
                AnsiConsole.Status()
                    .Start("Building project...", ctx =>
                    {
                        var result = _processRunner.RunProcess("dotnet", new[] { "build", workDir });
                        if (result != null)
                        {
                            throw new GhostException("Failed to build project");
                        }
                    });

                // Run application
                return await _processRunner.RunWithArgsAsync("dotnet", new[] { "run", "--project", workDir }, args, workDir);
            }
            finally
            {
                // Cleanup
                try
                {
                    Directory.Delete(workDir, recursive: true);
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }
}