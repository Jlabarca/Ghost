using Ghost.Infrastructure;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Ghost.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Ghost.Commands
{
    public class InstallCommand : Command<InstallCommand.Settings>
    {
        private readonly ProcessRunner _processRunner;
        private readonly ConfigManager _configManager;
        private readonly WorkspaceManager _workspaceManager;

        public class Settings : CommandSettings
        {
            [CommandOption("--force")]
            [Description("Force reinstallation even if already installed")]
            public bool Force { get; set; }

            [CommandOption("--no-shell-setup")]
            [Description("Skip shell integration setup")]
            public bool NoShellSetup { get; set; }

            [CommandOption("--workspace <PATH>")]
            [Description("Custom workspace path")]
            public string WorkspacePath { get; set; }
        }

        public InstallCommand(
            ProcessRunner processRunner,
            ConfigManager configManager,
            WorkspaceManager workspaceManager)
        {
            _processRunner = processRunner;
            _configManager = configManager;
            _workspaceManager = workspaceManager;
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

                // Determine platform
                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                
                // Calculate paths
                var (configDir, workspaceDir, shellConfigPath) = GetPlatformPaths(settings.WorkspacePath);

                // Create installation status
                var installSteps = new List<string>
                {
                    "Creating directories",
                    "Setting up configuration",
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
                    // Step 1: Create directories
                    var createDirsTask = ctx.AddTask("Creating directories");
                    await CreateDirectories(configDir, workspaceDir);
                    createDirsTask.Value = 100;

                    // Step 2: Set up configuration
                    var configTask = ctx.AddTask("Setting up configuration");
                    await SetupConfiguration(configDir, workspaceDir);
                    configTask.Value = 100;

                    // Step 3: Configure workspace
                    var workspaceTask = ctx.AddTask("Configuring workspace");
                    await ConfigureWorkspace(workspaceDir);
                    workspaceTask.Value = 100;

                    // Step 4: Shell integration
                    if (!settings.NoShellSetup)
                    {
                        var shellTask = ctx.AddTask("Setting up shell integration");
                        await SetupShellIntegration(isWindows, shellConfigPath);
                        shellTask.Value = 100;
                    }
                });

                // Show success message with next steps
                ShowSuccessMessage(isWindows, shellConfigPath);

                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error during installation:[/] {ex.Message}");
                return 1;
            }
        }

        private (string configDir, string workspaceDir, string shellConfigPath) GetPlatformPaths(string customWorkspace)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var documentsFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                
                return (
                    Path.Combine(localAppData, "Ghost"),
                    customWorkspace ?? Path.Combine(localAppData, "Ghost", "workspace"),
                    Path.Combine(documentsFolder, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")
                );
            }
            else
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var isZsh = File.Exists(Path.Combine(home, ".zshrc"));
                
                return (
                    Path.Combine(home, ".config", "ghost"),
                    customWorkspace ?? Path.Combine(home, ".ghost", "workspace"),
                    isZsh ? Path.Combine(home, ".zshrc") : Path.Combine(home, ".bashrc")
                );
            }
        }

        private async Task CreateDirectories(string configDir, string workspaceDir)
        {
            Directory.CreateDirectory(configDir);
            Directory.CreateDirectory(workspaceDir);

            // Set permissions
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await _processRunner.RunProcessAsync("chmod", new[] { "755", configDir });
                await _processRunner.RunProcessAsync("chmod", new[] { "700", workspaceDir });
            }
        }

        private async Task SetupConfiguration(string configDir, string workspaceDir)
        {
            var configPath = Path.Combine(configDir, "config.lua");
            if (!File.Exists(configPath))
            {
                var config = $@"-- Ghost Configuration File
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

        private async Task ConfigureWorkspace(string workspaceDir)
        {
            // Ensure workspace exists and has correct permissions
            if (!Directory.Exists(workspaceDir))
            {
                Directory.CreateDirectory(workspaceDir);
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    await _processRunner.RunProcessAsync("chmod", new[] { "700", workspaceDir });
                }
            }
        }

        private async Task SetupShellIntegration(bool isWindows, string shellConfigPath)
        {
            if (isWindows)
            {
                await SetupPowerShellIntegration(shellConfigPath);
            }
            else
            {
                await SetupUnixShellIntegration(shellConfigPath);
            }
        }

        private async Task SetupPowerShellIntegration(string profilePath)
        {
            var completion = @"
Register-ArgumentCompleter -Native -CommandName ghost -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)
    
    $commands = @(
        'run'
        'create'
        'alias'
        'push'
        'clean'
        'install'
    )
    
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

            var profileDir = Path.GetDirectoryName(profilePath);
            if (!string.IsNullOrEmpty(profileDir))
            {
                Directory.CreateDirectory(profileDir);
            }

            if (!File.Exists(profilePath))
            {
                await File.WriteAllTextAsync(profilePath, completion);
            }
            else if (!File.ReadAllText(profilePath).Contains("ghost -ScriptBlock"))
            {
                await File.AppendAllTextAsync(profilePath, completion);
            }
        }

        private async Task SetupUnixShellIntegration(string rcPath)
        {
            var completion = @"
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
            if [ -f ""$HOME/.ghost/aliases.json"" ]; then
                local aliases=$(jq -r 'keys[]' < ""$HOME/.ghost/aliases.json"" 2>/dev/null)
                COMPREPLY=( $(compgen -W ""${aliases}"" -- ${cur}) )
            fi
            return 0
            ;;
        *)
            ;;
    esac
}

complete -F _ghost_completion ghost";

            var completionDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local/share/bash-completion/completions");

            Directory.CreateDirectory(completionDir);
            var completionPath = Path.Combine(completionDir, "ghost");
            await File.WriteAllTextAsync(completionPath, completion);
            await _processRunner.RunProcessAsync("chmod", new[] { "644", completionPath });

            if (!File.ReadAllText(rcPath).Contains("_ghost_completion"))
            {
                var source = $"\n[ -f {completionPath} ] && source {completionPath}\n";
                await File.AppendAllTextAsync(rcPath, source);
            }
        }

        private void ShowSuccessMessage(bool isWindows, string shellConfigPath)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(
                Align.Left(
                    new Markup($@"Ghost CLI has been installed successfully!

To complete the setup:

1. Restart your terminal or run:
   [grey]{(isWindows ? ". $PROFILE" : "source " + shellConfigPath)}[/]

2. Test the installation with:
   [grey]ghost --version[/]

3. Start using Ghost:
   [grey]ghost create myapp[/]
   [grey]ghost run myapp[/]")
                ))
                .Header("[green]Installation Complete![/]")
                .BorderColor(Color.Green));
        }
    }
}