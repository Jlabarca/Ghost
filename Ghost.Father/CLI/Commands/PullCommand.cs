using System.Diagnostics;
using Ghost.Exceptions;
using Ghost.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
namespace Ghost.Father.CLI.Commands;

public class PullCommand : AsyncCommand<PullCommand.Settings>
{
    private const string GHOSTS_FOLDER = "ghosts";
    private readonly IGhostBus _bus;

    public PullCommand(IGhostBus bus)
    {
        _bus = bus;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        if (string.IsNullOrEmpty(settings.Repository))
        {
            if (!await IsGitRepo())
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Not a git repository");
                return 1;
            }
            // Pull in current directory
            return await PullCurrentRepository(settings);
        }

        // Clone new repository into the ghosts folder
        return await CloneRepository(settings);
    }

    private async Task<int> PullCurrentRepository(Settings settings)
    {
        try
        {
            // Check for uncommitted changes
            if (await HasChanges())
            {
                string? status = await GetGitStatus();
                AnsiConsole.MarkupLine("\n[yellow]Uncommitted changes:[/]");
                AnsiConsole.Write(new Panel(status)
                        .Border(BoxBorder.Rounded)
                        .BorderStyle(Style.Parse("grey")));

                if (!settings.Force)
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] Cannot pull with uncommitted changes. Use --force to override.");
                    return 1;
                }

                if (!AnsiConsole.Confirm("[yellow]Stash changes and continue?[/]"))
                {
                    return 1;
                }

                await RunGitCommand("stash");
            }

            // Pull changes
            await AnsiConsole.Status()
                    .StartAsync("Pulling changes...", async ctx =>
                    {
                        string? pullCommand = $"pull origin {settings.Branch}";
                        if (settings.Force)
                        {
                            pullCommand += " --force";
                        }

                        await RunGitCommand(pullCommand);
                    });

            AnsiConsole.MarkupLine("[green]Successfully pulled changes[/]");

            // Run app if requested
            if (settings.RunAfterPull)
            {
                string? appName = Path.GetFileName(Directory.GetCurrentDirectory());
                await RunApp(appName);
            }

            return 0;
        }
        catch (Exception ex) when (ex is GhostException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task<int> CloneRepository(Settings settings)
    {
        try
        {
            // Extract app name from repository URL
            string? appName = Path.GetFileNameWithoutExtension(settings.Repository)
                    .Replace(".git", "", StringComparison.OrdinalIgnoreCase);

            // Create the ghosts directory
            string? ghostsPath = Path.Combine(AppContext.BaseDirectory, GHOSTS_FOLDER);
            Directory.CreateDirectory(ghostsPath);

            string? targetDir = Path.Combine(ghostsPath, appName);

            // Check if directory already exists
            if (Directory.Exists(targetDir) && !settings.Force)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Directory {targetDir} already exists. Use --force to override.");
                return 1;
            }

            // Delete the directory if force is specified
            if (Directory.Exists(targetDir) && settings.Force)
            {
                Directory.Delete(targetDir, true);
            }

            // Clone repository
            await AnsiConsole.Status()
                    .StartAsync($"Cloning {appName}...", async ctx =>
                    {
                        string? cloneCommand = $"clone {settings.Repository} {targetDir}";
                        if (settings.Branch != "main")
                        {
                            cloneCommand += $" -b {settings.Branch}";
                        }

                        await RunGitCommand(cloneCommand);
                    });

            AnsiConsole.MarkupLine($"[green]Successfully cloned {appName} to {targetDir}[/]");

            // Run app if requested
            if (settings.RunAfterPull)
            {
                await RunApp(appName);
            }

            return 0;
        }
        catch (Exception ex) when (ex is GhostException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task RunApp(string appName)
    {
        SystemCommand? command = new SystemCommand
        {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = "run",
                Parameters = new Dictionary<string, string>
                {
                        ["appId"] = appName
                }
        };

        await _bus.PublishAsync("ghost:commands", command);
        AnsiConsole.MarkupLine($"[grey]Started app:[/] {appName}");
    }

    private static async Task<bool> IsGitRepo()
    {
        try
        {
            await RunGitCommand("rev-parse --git-dir");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasChanges()
    {
        string? output = await RunGitCommandWithOutput("status --porcelain");
        return !string.IsNullOrWhiteSpace(output);
    }

    private static async Task<string> GetGitStatus()
    {
        return await RunGitCommandWithOutput("status --short");
    }

    private static async Task RunGitCommand(string args)
    {
        ProcessStartInfo? startInfo = new ProcessStartInfo
        {
                FileName = "git",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
        };

        Process? process = Process.Start(startInfo);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            string? error = await process.StandardError.ReadToEndAsync();
            throw new GhostException($"Git command failed: {error}", ErrorCode.GitError);
        }
    }

    private static async Task<string> RunGitCommandWithOutput(string args)
    {
        ProcessStartInfo? startInfo = new ProcessStartInfo
        {
                FileName = "git",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
        };

        Process? process = Process.Start(startInfo);
        string? output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            string? error = await process.StandardError.ReadToEndAsync();
            throw new GhostException($"Git command failed: {error}", ErrorCode.GitError);
        }

        return output;
    }

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[repository]")]
        public string Repository { get; set; }

        [CommandOption("--branch")]
        public string Branch { get; set; } = "main";

        [CommandOption("--run")]
        public bool RunAfterPull { get; set; }

        [CommandOption("--force")]
        public bool Force { get; set; }
    }
}
