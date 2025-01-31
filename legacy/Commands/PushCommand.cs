using Ghost.Legacy.Infrastructure;
using Ghost.Legacy.Services;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Ghost.Legacy.Commands;

public class PushCommand : AsyncCommand<PushCommand.Settings>
{
    private readonly ConfigManager _configManager;
    private readonly GithubService _githubService;
    private readonly ProcessRunner _processRunner;
    private readonly AliasCommand _aliasCommand;

    public class Settings : CommandSettings
    {
        [CommandArgument(0, "<Name>")]
        [Description("The name of the application to push.")]
        public string Name { get; set; }

        [CommandOption("--token")]
        [Description("GitHub personal access token. If not provided, will check .ghost file.")]
        public string Token { get; set; }

        [CommandOption("--alias")]
        [Description("Create an alias for the application after pushing.")]
        public bool CreateAlias { get; set; }
    }

    public PushCommand(ConfigManager configManager, GithubService githubService, ProcessRunner processRunner, AliasCommand aliasCommand)
    {
        _configManager = configManager;
        _githubService = githubService;
        _processRunner = processRunner;
        _aliasCommand = aliasCommand;
    }

    private (string username, string email) GetGitConfig(string workDir)
    {
        // Try to get existing git config
        var nameResult = _processRunner.RunProcess("git", new[] { "config", "user.name" }, workDir);
        var emailResult = _processRunner.RunProcess("git", new[] { "config", "user.email" }, workDir);

        string username = nameResult?.StandardOutput?.Trim();
        string email = emailResult?.StandardOutput?.Trim();

        // If either is missing, prompt for both
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(email))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Git configuration required[/]");
            username = AnsiConsole.Ask<string>("Enter your name for Git:");
            email = AnsiConsole.Ask<string>("Enter your email for Git:");
            AnsiConsole.WriteLine();
        }

        return (username, email);
    }

    private void RunGitCommand(string[] args, string workDir, string errorContext)
    {
        var result = _processRunner.RunProcess("git", args, workDir);
        if (result.ExitCode != 0)
        {
            var errorMessage = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;

            throw new GhostException(
                $"Git operation failed during {errorContext}:\n{errorMessage}",
                ErrorCode.GithubError);
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[yellow]Pushing {settings.Name} to GitHub[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();

            // Check if directory exists
            var workDir = Path.GetFullPath(settings.Name);
            if (!Directory.Exists(workDir))
            {
                throw new GhostException($"Directory '{settings.Name}' not found. Make sure you're in the correct directory.", ErrorCode.DirectoryNotFound);
            }

            // Get GitHub token
            var token = settings.Token ?? _configManager.GetSetting("githubToken");
            if (string.IsNullOrEmpty(token))
            {
                throw new GhostException(
                    "GitHub token not provided and not found in .ghost file.\n" +
                    "Either use --token option or create a .ghost file with:\n" +
                    "{\"githubToken\": \"your-token-here\"}",
                    ErrorCode.TokenNotFound);
            }

            // Get Git configuration before starting any status displays
            var (username, email) = GetGitConfig(workDir);
            string repoUrl = null;

            var status = AnsiConsole.Status();
            await status.StartAsync("Setting up repository...", async ctx =>
            {
                // Create GitHub repository
                ctx.Status("Creating GitHub repository...");
                repoUrl = await _githubService.CreateRepositoryAsync(settings.Name, token);
                AnsiConsole.MarkupLine($"Repository created at: [blue]{repoUrl}[/]");

                // Initialize git
                ctx.Status("Initializing git repository...");
                if (!Directory.Exists(Path.Combine(workDir, ".git")))
                {
                    // Initialize without specifying branch name
                    RunGitCommand(new[] { "init" }, workDir, "repository initialization");
                }

                // Configure Git
                ctx.Status("Configuring git...");
                RunGitCommand(new[] { "config", "user.name", username }, workDir, "user name configuration");
                RunGitCommand(new[] { "config", "user.email", email }, workDir, "user email configuration");

                // Set default branch name to main
                RunGitCommand(new[] { "checkout", "-b", "main" }, workDir, "branch creation");

                // Setup remote
                ctx.Status("Setting up remote...");
                try
                {
                    RunGitCommand(new[] { "remote", "remove", "origin" }, workDir, "remote removal");
                }
                catch { /* Ignore if remote doesn't exist */ }

                RunGitCommand(new[] { "remote", "add", "origin", repoUrl }, workDir, "remote addition");

                // Stage files
                ctx.Status("Adding files...");
                var addResult = _processRunner.RunProcess("git", new[] { "add", "." }, workDir);
                if (addResult.ExitCode != 0)
                {
                    // Check if there are any files to add
                    var statusResult = _processRunner.RunProcess("git", new[] { "status", "--porcelain" }, workDir);
                    if (string.IsNullOrWhiteSpace(statusResult.StandardOutput))
                    {
                        throw new GhostException(
                            "No files found to commit. Make sure your project has files.",
                            ErrorCode.GithubError);
                    }
                    else
                    {
                        throw new GhostException(
                            $"Failed to add files:\n{addResult.StandardError}",
                            ErrorCode.GithubError);
                    }
                }

                // Commit changes
                ctx.Status("Committing changes...");
                RunGitCommand(new[] { "commit", "-m", "\"Initial commit\"" }, workDir, "commit");

                // Get current branch name
                var branchResult = _processRunner.RunProcess("git", new[] { "rev-parse", "--abbrev-ref", "HEAD" }, workDir);
                var currentBranch = branchResult.StandardOutput.Trim();

                // Push to GitHub using current branch name
                ctx.Status($"Pushing to GitHub (branch: {currentBranch})...");
                RunGitCommand(new[] { "push", "-u", "origin", currentBranch }, workDir, "push");

                // Create alias if requested
                if (settings.CreateAlias)
                {
                    ctx.Status("Creating alias...");
                    _aliasCommand.Execute(context, new AliasCommand.Settings
                    {
                        Alias = settings.Name,
                        Url = repoUrl
                    });
                }
            });

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(
                Align.Left(
                    new Markup($"""
                        Your application is now on GitHub!
                        
                        Repository URL: [blue]{repoUrl}[/]
                        
                        {(settings.CreateAlias ? "[grey]Alias created! You may need to restart your terminal to use it.[/]" : "")}
                        """)
                ))
                .Header("[green]Successfully pushed![/]")
                .BorderColor(Color.Green));

            return 0;
        }
        catch (GhostException ex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(
                Align.Left(
                    new Markup($"[red]{ex.UserMessage}[/]")
                ))
                .Header("[red]Error[/]")
                .BorderColor(Color.Red));
            return 1;
        }
    }
}