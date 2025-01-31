using Ghost.Legacy.Infrastructure;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Ghost.Legacy.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ghost.Legacy.Commands;

public class InstallCommand : Command<InstallCommand.Settings>
{
    private readonly ProcessRunner _processRunner;

    public class Settings : CommandSettings
    {
        [CommandOption("--force")]
        [Description("Force reinstallation even if already installed")]
        public bool Force { get; set; }

        [CommandOption("--local")]
        [Description("Install locally in the current directory")]
        public bool Local { get; set; }

        [CommandOption("--version")]
        [Description("Specify version to install")]
        public string Version { get; set; }

        [CommandOption("--source")]
        [Description("Specify NuGet package source")]
        public string Source { get; set; }
    }

    public InstallCommand(
        ProcessRunner processRunner
    )
    {
        _processRunner = processRunner;
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        return ExecuteAsync(context, settings).GetAwaiter().GetResult();
    }

    public async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            AnsiConsole.Write(new Rule("[yellow]Installing Ghost CLI[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();

            var installSteps = new List<string>
            {
                "Checking installation status",
                "Installing .NET tool",
                "Configuring workspace",
                "Setting up shell integration"
            };

            var progress = AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn()
                });

            await progress.StartAsync(async ctx =>
            {
                // Step 1: Check existing installation
                var checkTask = ctx.AddTask("Checking installation status");
                var isInstalled = await CheckExistingInstallation();
                checkTask.Value = 100;

                if (isInstalled && !settings.Force)
                {
                    AnsiConsole.MarkupLine("[yellow]Ghost CLI is already installed. Use --force to reinstall.[/]");
                    return;
                }

                // Step 2: Install .NET tool
                var installTask = ctx.AddTask("Installing .NET tool");
                await InstallDotNetTool(settings);
                installTask.Value = 100;

                // Step 3: Configure workspace
                var workspaceTask = ctx.AddTask("Configuring workspace");
                await ConfigureWorkspace();
                workspaceTask.Value = 100;

                // Step 4: Setup shell completion
                var shellTask = ctx.AddTask("Setting up shell integration");
                await SetupShellCompletion();
                shellTask.Value = 100;
            });

            ShowSuccessMessage();
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during installation:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task<bool> CheckExistingInstallation()
    {
        try
        {
            var result = await _processRunner.RunProcessAsync("dotnet", new[] { "tool", "list", "--global" });
            return result.StandardOutput.Contains("ghost");
        }
        catch
        {
            return false;
        }
    }

    private async Task InstallDotNetTool(Settings settings)
    {
        var args = new List<string> { "tool", settings.Local ? "install" : "install", "--global" };

        if (!string.IsNullOrEmpty(settings.Version))
        {
            args.Add("--version");
            args.Add(settings.Version);
        }

        if (!string.IsNullOrEmpty(settings.Source))
        {
            args.Add("--add-source");
            args.Add(settings.Source);
        }

        args.Add("Ghost");

        var result = await _processRunner.RunProcessAsync("dotnet", args.ToArray());
        if (!result.Success)
        {
            throw new GhostException($"Failed to install Ghost CLI: {result.StandardError}");
        }
    }

    private async Task ConfigureWorkspace()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ghost");
        var workspaceDir = Path.Combine(configDir, "workspace");

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(workspaceDir);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await _processRunner.RunProcessAsync("chmod", new[] { "700", workspaceDir });
        }

        // Initialize config file
        var configPath = Path.Combine(configDir, "config.lua");
        if (!File.Exists(configPath))
        {
            var config = $@"
-- Ghost Configuration File
config = {{
    settings = {{
        workspace = {{
            path = ""{workspaceDir.Replace("\\", "\\\\")}"",
            maxApps = 10,
            cleanupAge = 7
        }}
    }},
    aliases = {{}}
}}

function addAlias(name, url)
    config.aliases[name] = url
end

function removeAlias(name)
    config.aliases[name] = nil
end

function setWorkspace(path)
    config.settings.workspace.path = path
end

return config";
            await File.WriteAllTextAsync(configPath, config);
        }
    }

    private async Task SetupShellCompletion()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await SetupPowerShellCompletion();
        }
        else
        {
            await SetupBashCompletion();
        }
    }

    private async Task SetupPowerShellCompletion()
    {
        var profilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "WindowsPowerShell",
            "Microsoft.PowerShell_profile.ps1");

        var completion = @"
Register-ArgumentCompleter -Native -CommandName ghost -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)
    
    $commands = @('run', 'create', 'alias', 'push', 'clean', 'install')
    $completions = @()
    
    foreach ($command in $commands) {
        if ($command.StartsWith($wordToComplete)) {
            $completions += [System.Management.Automation.CompletionResult]::new(
                $command,
                $command,
                'ParameterValue',
                $command
            )
        }
    }
    
    return $completions
}";

        Directory.CreateDirectory(Path.GetDirectoryName(profilePath));
        await File.AppendAllTextAsync(profilePath, completion);
    }

    private async Task SetupBashCompletion()
    {
        var completionScript = @"
_ghost_completion() {
    local cur prev opts
    COMPREPLY=()
    cur=""${COMP_WORDS[COMP_CWORD]}""
    prev=""${COMP_WORDS[COMP_CWORD-1]}""
    opts=""run create alias push clean install""

    case ""${prev}"" in
        ghost)
            COMPREPLY=( $(compgen -W ""${opts}"" -- ${cur}) )
            return 0
            ;;
        run)
            # Add alias completion here if needed
            return 0
            ;;
        *)
            ;;
    esac
}

complete -F _ghost_completion ghost";

        var completionPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ghost-completion.sh")
            : "/etc/bash_completion.d/ghost";

        await File.WriteAllTextAsync(completionPath, completionScript);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await _processRunner.RunProcessAsync("chmod", new[] { "644", completionPath });
        }
    }

    private void ShowSuccessMessage()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            Align.Left(
                new Markup(@"Ghost CLI has been installed successfully!

To complete the setup:
1. Restart your terminal or reload your shell configuration
2. Test the installation with: [grey]ghost --version[/]
3. Start using Ghost:
   [grey]ghost create myapp[/]
   [grey]ghost run myapp[/]

For more information, visit: https://github.com/yourusername/ghost-cli")))
            .Header("[green]Installation Complete![/]")
            .BorderColor(Color.Green));
    }
}