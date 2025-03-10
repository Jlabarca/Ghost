using Ghost.Core.Exceptions;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class PushCommand : AsyncCommand<PushCommand.Settings>
{
    public class Settings : CommandSettings
    {
        [CommandArgument(0, "[remote]")]
        public string Remote { get; set; }

        [CommandOption("--branch")]
        public string Branch { get; set; } = "main";

        [CommandOption("--message")]
        public string Message { get; set; } = "Update Ghost app";

        [CommandOption("--force")]
        public bool Force { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Verify we're in a git repository
            if (!await GitCommandExists())
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Git is not installed or not in PATH");
                return 1;
            }

            if (!await IsGitRepo())
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Not a git repository");
                return 1;
            }

            // Add remote if provided
            if (!string.IsNullOrEmpty(settings.Remote))
            {
                var hasRemote = await HasRemote("origin");
                if (!hasRemote)
                {
                    await RunGitCommand($"remote add origin {settings.Remote}");
                    AnsiConsole.MarkupLine($"[grey]Added remote:[/] {settings.Remote}");
                }
            }

            // Display status before pushing
            var status = await GetGitStatus();
            if (!string.IsNullOrWhiteSpace(status))
            {
                AnsiConsole.MarkupLine("\n[yellow]Changes to be pushed:[/]");
                AnsiConsole.Write(new Panel(status)
                    .Border(BoxBorder.Rounded)
                    .BorderStyle(Style.Parse("grey")));
            }

            // Confirm with user
            if (!AnsiConsole.Confirm("[yellow]Push these changes?[/]"))
            {
                return 0;
            }

            // Stage and commit changes
            if (await HasChanges())
            {
                await RunGitCommand("add .");
                await RunGitCommand($"commit -m \"{settings.Message}\"");
            }

            // Push changes
            var pushCommand = $"push origin {settings.Branch}";
            if (settings.Force)
            {
                pushCommand += " --force";
            }

            await AnsiConsole.Status()
                .StartAsync("Pushing changes...", async ctx =>
                {
                    await RunGitCommand(pushCommand);
                });

            AnsiConsole.MarkupLine("[green]Successfully pushed changes[/]");
            return 0;
        }
        catch (Exception ex) when (ex is GhostException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private static async Task<bool> GitCommandExists()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo);
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
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

    private static async Task<bool> HasRemote(string name)
    {
        try
        {
            await RunGitCommand($"remote get-url {name}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> HasChanges()
    {
        var output = await RunGitCommandWithOutput("status --porcelain");
        return !string.IsNullOrWhiteSpace(output);
    }

    private static async Task<string> GetGitStatus()
    {
        return await RunGitCommandWithOutput("status --short");
    }

    private static async Task RunGitCommand(string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new GhostException($"Git command failed: {error}", ErrorCode.GitError);
        }
    }

    private static async Task<string> RunGitCommandWithOutput(string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(startInfo);
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            throw new GhostException($"Git command failed: {error}", ErrorCode.GitError);
        }

        return output;
    }
}