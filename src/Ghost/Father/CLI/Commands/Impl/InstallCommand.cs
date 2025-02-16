using Ghost.Core;
using Ghost.Core.Storage;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Ghost.Father.CLI.Commands;

public class InstallCommand : AsyncCommand<InstallCommand.Settings>
{
    private readonly IGhostBus _bus;

    public InstallCommand(IGhostBus bus)
    {
        _bus = bus;
    }

    public class Settings : CommandSettings
    {
        [CommandOption("--user")]
        public bool UserInstall { get; set; }

        [CommandOption("--no-cli")]
        public bool SkipCliInstall { get; set; }

        [CommandOption("--no-daemon")]
        public bool SkipDaemonInstall { get; set; }

        [CommandOption("--port")]
        public int? Port { get; set; }

        [CommandOption("--data-dir")]
        public string DataDir { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        try
        {
            // Check if running with admin/sudo
            if (!settings.UserInstall && !IsElevated())
            {
                AnsiConsole.MarkupLine("[red]Error:[/] This command requires administrative privileges. Use --user for user-level installation.");
                return 1;
            }

            // Get current executable path
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                throw new GhostException("Could not determine executable path", ErrorCode.ProcessError);
            }

            // Create default data directory if not specified
            var dataDir = settings.DataDir ?? Path.Combine(
                settings.UserInstall ?
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) :
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Ghost"
            );

            // Create installation directories
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(Path.Combine(dataDir, "logs"));
            Directory.CreateDirectory(Path.Combine(dataDir, "data"));
            Directory.CreateDirectory(Path.Combine(dataDir, "apps"));

            // Install components
            if (!settings.SkipDaemonInstall)
            {
                await InstallDaemon(exePath, settings.UserInstall, dataDir, settings.Port);
            }

            if (!settings.SkipCliInstall)
            {
                await InstallCli(exePath, settings.UserInstall);
            }

            // Create configuration
            await CreateConfiguration(dataDir, settings);

            AnsiConsole.MarkupLine("""

                [green]Ghost installed successfully![/]

                Data directory: {0}
                CLI Command: ghost
                Service Name: GhostFatherDaemon
                """, dataDir);

            if (settings.UserInstall && !OperatingSystem.IsWindows())
            {
                AnsiConsole.MarkupLine("""
                    
                    [yellow]Note:[/] You may need to restart your shell or run:
                    source ~/.profile
                    """);
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private async Task InstallDaemon(string sourcePath, bool userInstall, string dataDir, int? port)
    {
        if (OperatingSystem.IsWindows())
        {
            await InstallWindowsService(sourcePath, userInstall, dataDir, port);
        }
        else
        {
            await InstallSystemdService(sourcePath, userInstall, dataDir, port);
        }

        // Register GhostFather in process list
        await RegisterGhostFather();
    }

    private async Task InstallWindowsService(string sourcePath, bool userInstall, string dataDir, int? port)
    {
        var serviceName = "GhostFatherDaemon";
        var displayName = "Ghost Process Manager";
        var description = "Ghost application orchestration and process management service";

        var serviceExePath = Path.Combine(
            userInstall ?
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) :
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Ghost",
            "ghostd.exe"
        );

        await AnsiConsole.Status()
            .StartAsync("Installing Ghost service...", async ctx =>
            {
                // Create installation directory
                Directory.CreateDirectory(Path.GetDirectoryName(serviceExePath));

                // Copy daemon executable
                File.Copy(sourcePath, serviceExePath, true);

                // Build service arguments
                var args = new List<string>
                {
                    "--daemon",
                    $"--data-dir \"{dataDir}\""
                };
                if (port.HasValue)
                {
                    args.Add($"--port {port.Value}");
                }

                // Install service
                var sc = new ProcessStartInfo
                {
                    FileName = "sc",
                    Arguments = $"create {serviceName} binPath= \"{serviceExePath} {string.Join(" ", args)}\" start= auto DisplayName= \"{displayName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                var process = Process.Start(sc);
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    throw new GhostException(
                        $"Failed to install service: {await process.StandardError.ReadToEndAsync()}",
                        ErrorCode.ProcessError);
                }

                // Set description
                sc.Arguments = $"description {serviceName} \"{description}\"";
                process = Process.Start(sc);
                await process.WaitForExitAsync();

                // Start service
                sc.Arguments = $"start {serviceName}";
                process = Process.Start(sc);
                await process.WaitForExitAsync();
            });
    }

    private async Task InstallSystemdService(string sourcePath, bool userInstall, string dataDir, int? port)
    {
        var serviceExePath = userInstall ?
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "ghostd") :
            "/usr/local/bin/ghostd";

        var serviceFile = userInstall ?
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user", "ghost.service") :
            "/etc/systemd/system/ghost.service";

        await AnsiConsole.Status()
            .StartAsync("Installing Ghost service...", async ctx =>
            {
                // Create installation directories
                Directory.CreateDirectory(Path.GetDirectoryName(serviceExePath));
                Directory.CreateDirectory(Path.GetDirectoryName(serviceFile));

                // Copy daemon executable
                File.Copy(sourcePath, serviceExePath, true);
                await RunCommand("chmod", $"+x {serviceExePath}");

                // Build service arguments
                var args = new List<string>
                {
                    "--daemon",
                    $"--data-dir {dataDir}"
                };
                if (port.HasValue)
                {
                    args.Add($"--port {port.Value}");
                }

                // Create service file
                var serviceContent = $"""
                [Unit]
                Description=Ghost Process Manager
                After=network.target

                [Service]
                ExecStart={serviceExePath} {string.Join(" ", args)}
                Restart=always
                RestartSec=10
                Environment=DOTNET_ROOT={Environment.GetEnvironmentVariable("DOTNET_ROOT")}

                [Install]
                WantedBy={(userInstall ? "default" : "multi-user")}.target
                """;

                await File.WriteAllTextAsync(serviceFile, serviceContent);

                if (userInstall)
                {
                    // User service
                    await RunCommand("systemctl", "--user daemon-reload");
                    await RunCommand("systemctl", "--user enable ghost");
                    await RunCommand("systemctl", "--user start ghost");
                }
                else
                {
                    // System service
                    await RunCommand("systemctl", "daemon-reload");
                    await RunCommand("systemctl", "enable ghost");
                    await RunCommand("systemctl", "start ghost");
                }
            });
    }

    private async Task InstallCli(string sourcePath, bool userInstall)
    {
        if (OperatingSystem.IsWindows())
        {
            await InstallWindowsCli(sourcePath, userInstall);
        }
        else
        {
            await InstallUnixCli(sourcePath, userInstall);
        }
    }

    private async Task InstallWindowsCli(string sourcePath, bool userInstall)
    {
        var targetPath = Path.Combine(
            userInstall ?
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) :
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Ghost",
            "ghost.exe"
        );

        await AnsiConsole.Status()
            .StartAsync("Installing Ghost CLI...", async ctx =>
            {
                // Create installation directory
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

                // Copy CLI executable
                File.Copy(sourcePath, targetPath, true);

                // Add to PATH
                var path = userInstall ?
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) :
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);

                var paths = (path ?? "").Split(Path.PathSeparator).ToList();
                var targetDir = Path.GetDirectoryName(targetPath);

                if (!paths.Contains(targetDir))
                {
                    paths.Add(targetDir);
                    var newPath = string.Join(Path.PathSeparator, paths);

                    Environment.SetEnvironmentVariable(
                        "PATH",
                        newPath,
                        userInstall ? EnvironmentVariableTarget.User : EnvironmentVariableTarget.Machine
                    );
                }
            });
    }

    private async Task InstallUnixCli(string sourcePath, bool userInstall)
    {
        var targetPath = userInstall ?
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "ghost") :
            "/usr/local/bin/ghost";

        await AnsiConsole.Status()
            .StartAsync("Installing Ghost CLI...", async ctx =>
            {
                // Create installation directory
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));

                // Copy CLI executable
                File.Copy(sourcePath, targetPath, true);
                await RunCommand("chmod", $"+x {targetPath}");

                if (userInstall)
                {
                    // Add .local/bin to PATH if not present
                    var profileFile = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".profile"
                    );

                    var pathLine = "export PATH=\"$HOME/.local/bin:$PATH\"";
                    var profileContent = File.Exists(profileFile) ? await File.ReadAllTextAsync(profileFile) : "";

                    if (!profileContent.Contains(pathLine))
                    {
                        await File.AppendAllTextAsync(profileFile, $"\n{pathLine}\n");
                    }
                }
            });
    }

    private async Task CreateConfiguration(string dataDir, Settings settings)
    {
        var configFile = Path.Combine(dataDir, ".ghost.yaml");
        var config = $"""
            system:
              id: ghost
              mode: daemon
              port: {settings.Port ?? 31337}
              data_dir: {dataDir}

            monitoring:
              enabled: true
              interval: "00:00:05"
              retention_days: 7

            logging:
              level: information
              file_enabled: true
              console_enabled: true
              retention_days: 30

            security:
              allow_remote: false
              require_auth: false
            """;

        await File.WriteAllTextAsync(configFile, config);
    }

    private async Task RegisterGhostFather()
    {
        try
        {
            // Wait for daemon to be ready
            await Task.Delay(2000);

            var command = new SystemCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                CommandType = "register",
                Parameters = new Dictionary<string, string>
                {
                    ["appId"] = "GhostFather",
                    ["type"] = "daemon",
                    ["description"] = "Ghost Process Manager",
                    ["autoStart"] = "true",
                    ["isPersistent"] = "true"
                }
            };

            // Send registration command
            await _bus.PublishAsync("ghost:commands", command);

            // Wait for response
            var responseReceived = new TaskCompletionSource<bool>();
            await foreach (var response in _bus.SubscribeAsync<CommandResponse>("ghost:responses"))
            {
                if (response.CommandId == command.CommandId)
                {
                    if (!response.Success)
                    {
                        throw new GhostException($"Failed to register GhostFather: {response.Error}");
                    }
                    responseReceived.SetResult(true);
                    break;
                }
            }

            await responseReceived.Task;
        }
        catch (Exception ex)
        {
            G.LogWarn($"Failed to register GhostFather in process list: {ex.Message}");
            // Don't throw - this is non-critical
        }
    }

    private static async Task RunCommand(string command, string args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var process = Process.Start(startInfo);
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new GhostException(
                $"Command failed: {await process.StandardError.ReadToEndAsync()}",
                ErrorCode.ProcessError);
        }
    }

    private static bool IsElevated()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        else
        {
            return geteuid() == 0;
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();
}