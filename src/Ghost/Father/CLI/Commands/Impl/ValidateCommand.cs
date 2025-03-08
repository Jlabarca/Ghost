using Ghost.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class ValidateCommand : AsyncCommand<ValidateCommand.Settings>
{
  private readonly IServiceCollection _services;
  private readonly IGhostBus _bus;

  public class Settings : CommandSettings
  {
    [CommandOption("--fix")]
    [Description("Attempt to fix any issues found")]
    public bool Fix { get; set; }

    [CommandOption("--verbose")]
    [Description("Show detailed validation output")]
    public bool Verbose { get; set; }
  }

  public ValidateCommand(IServiceCollection services, IGhostBus bus)
  {
    _services = services;
    _bus = bus;
  }

  public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
  {
    var hasErrors = false;

    if (settings.Verbose)
    {
      G.LogInfo("Starting Ghost validation with verbose output");
      G.LogInfo("Validation will check: Commands, Installation, Environment, and Integration");
    }

    await AnsiConsole.Status()
        .StartAsync("Validating Ghost installation...", async ctx =>
        {
          // Command validation
          if (settings.Verbose) G.LogInfo("Starting command validation...");
          ctx.Status("Validating commands...");
          var commandValidator = new CommandValidator(_services, _bus);

          // Register all commands from registry
          if (settings.Verbose) G.LogInfo("Registering commands from registry...");
          CommandRegistry.RegisterWithValidator(commandValidator);

          if (settings.Verbose) G.LogInfo("Validating command configurations...");
          var cmdResults = commandValidator.ValidateCommands();

          // Add registry-specific validation
          if (settings.Verbose) G.LogInfo("Performing registry-specific validation...");
          ValidateCommandRegistry(cmdResults);

          if (settings.Verbose)
          {
            G.LogInfo($"Command validation completed with {cmdResults.GetIssueCount()} issues");
            foreach (var issue in cmdResults.GetIssues())
            {
              G.LogInfo($"- {issue.Type}: {issue.CommandName} - {issue.Message}");
            }
          }

          cmdResults.PrintResults();
          if (!cmdResults.IsValid)
          {
            hasErrors = true;
            G.LogWarn("Command validation failed");
          } else if (settings.Verbose)
          {
            G.LogInfo("Command validation succeeded");
          }

          // Installation validation
          if (settings.Verbose) G.LogInfo("Starting installation validation...");
          ctx.Status("Checking installation state...");
          var installResults = await ValidateInstallationAsync(settings);
          if (!installResults.IsValid)
          {
            hasErrors = true;
            if (settings.Fix)
            {
              if (settings.Verbose) G.LogInfo("Attempting to fix installation issues...");
              ctx.Status("Attempting to fix installation issues...");
              await FixInstallationIssuesAsync(installResults);
            }
          } else if (settings.Verbose)
          {
            G.LogInfo("Installation validation succeeded");
          }

          // Environment validation
          if (settings.Verbose) G.LogInfo("Starting environment validation...");
          ctx.Status("Validating environment...");
          var envResults = await ValidateEnvironmentAsync();
          if (!envResults.IsValid)
          {
            hasErrors = true;
            if (settings.Verbose)
            {
              G.LogWarn("Environment validation failed");
              foreach (var issue in envResults.GetIssues())
              {
                G.LogWarn($"- {issue.Type}: {issue.Message}");
              }
            }
          } else if (settings.Verbose)
          {
            G.LogInfo("Environment validation succeeded");
          }

          // Integration validation
          if (settings.Verbose)
          {
            G.LogInfo("Starting integration tests...");
            ctx.Status("Testing Ghost integration...");
            var integrationResults = await ValidateIntegrationAsync();
            if (!integrationResults.IsValid)
            {
              hasErrors = true;
              G.LogWarn("Integration validation failed");
              foreach (var issue in integrationResults.GetIssues())
              {
                G.LogWarn($"- {issue.Type}: {issue.Message}");
              }
            } else
            {
              G.LogInfo("Integration validation succeeded");
            }
          }
        });

    if (settings.Verbose)
    {
      G.LogInfo($"Validation completed with {(hasErrors ? "errors" : "success")}");
    }

    return hasErrors ? 1 : 0;
  }

  private void ValidateCommandRegistry(ValidationResult result)
  {
    var commands = CommandRegistry.GetCommands().ToList();

    // Check for duplicate command names
    var duplicates = commands
        .GroupBy(c => c.Name.ToLowerInvariant())
        .Where(g => g.Count() > 1)
        .ToList();

    foreach (var dupe in duplicates)
    {
      result.AddError(
          typeof(ValidateCommand),
          "CommandRegistry",
          $"Duplicate command name '{dupe.Key}' used by: {string.Join(", ", dupe.Select(d => d.CommandType.Name))}");
    }

    // Validate each command definition
    foreach (var cmd in commands)
    {
      // Check description
      if (string.IsNullOrWhiteSpace(cmd.Description))
      {
        result.AddWarning(
            cmd.CommandType,
            cmd.Name,
            "Command lacks description");
      }

      // Check examples
      if (!cmd.Examples.Any())
      {
        result.AddWarning(
            cmd.CommandType,
            cmd.Name,
            "Command has no usage examples");
      }

      // Validate command type
      if (!typeof(ICommand).IsAssignableFrom(cmd.CommandType) &&
          !typeof(AsyncCommand).IsAssignableFrom(cmd.CommandType))
      {
        result.AddError(
            cmd.CommandType,
            cmd.Name,
            "Command type must inherit from ICommand or AsyncCommand");
      }

      // Validate settings type if present
      var settingsProperty = cmd.CommandType.GetProperty("Settings");
      if (settingsProperty != null)
      {
        var settingsType = settingsProperty.PropertyType;
        if (!typeof(CommandSettings).IsAssignableFrom(settingsType))
        {
          result.AddError(
              cmd.CommandType,
              cmd.Name,
              $"Settings type {settingsType.Name} must inherit from CommandSettings");
        }
      }
    }
  }

  private async Task<ValidationResult> ValidateInstallationAsync(Settings settings)
  {
    var result = new ValidationResult(_bus);
    var installRoot = GetInstallRoot();

    // Check installation directories
    var requiredDirs = new[]
    {
        "logs", "data",
        "apps", "templates"
    };

    foreach (var dir in requiredDirs)
    {
      var path = Path.Combine(installRoot, dir);
      if (!Directory.Exists(path))
      {
        result.AddError(typeof(ValidateCommand), "Installation",
            $"Required directory missing: {dir}");
      }
    }

    // Check executables
    var ghostExe = OperatingSystem.IsWindows() ? "ghost.exe" : "ghost";
    var ghostdExe = OperatingSystem.IsWindows() ? "ghostd.exe" : "ghostd";

    if (!File.Exists(Path.Combine(installRoot, "bin", ghostExe)))
    {
      result.AddError(typeof(ValidateCommand), "Installation",
          $"Ghost CLI executable missing: {ghostExe}");
    }

    if (!File.Exists(Path.Combine(installRoot, "bin", ghostdExe)))
    {
      result.AddError(typeof(ValidateCommand), "Installation",
          $"Ghost daemon executable missing: {ghostdExe}");
    }

    // Check service installation
    var (serviceInstalled, serviceStatus) = await CheckServiceStatusAsync();
    if (!serviceInstalled)
    {
      result.AddError(typeof(ValidateCommand), "Service",
          "Ghost service is not installed");
    } else if (serviceStatus != "Running")
    {
      result.AddWarning(typeof(ValidateCommand), "Service",
          $"Ghost service is not running (current status: {serviceStatus})");
    }

    // Check Redis connection if configured
    try
    {
      var isAvailable = await _bus.IsAvailableAsync();
      if (!isAvailable)
      {
        result.AddWarning(typeof(ValidateCommand), "Redis",
            "Redis connection failed - distributed features will be unavailable");
      }
    }
    catch (Exception ex)
    {
      if (settings.Verbose)
      {
        result.AddWarning(typeof(ValidateCommand), "Redis",
            $"Redis error: {ex.Message}");
      }
    }

    return result;
  }

  private async Task<ValidationResult> ValidateEnvironmentAsync()
  {
    var result = new ValidationResult(_bus);

    // Check .NET version
    var dotnetVersion = Environment.Version;
    if (dotnetVersion.Major < 8)
    {
      result.AddError(typeof(ValidateCommand), "Environment",
          $"Requires .NET 8.0 or higher (current: {dotnetVersion})");
    }

    // Check PATH
    var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator);
    var ghostInPath = false;
    if (paths != null)
    {
      foreach (var path in paths)
      {
        var ghostPath = Path.Combine(path,
            OperatingSystem.IsWindows() ? "ghost.exe" : "ghost");
        if (File.Exists(ghostPath))
        {
          ghostInPath = true;
          break;
        }
      }
    }

    if (!ghostInPath)
    {
      result.AddWarning(typeof(ValidateCommand), "Environment",
          "Ghost is not in PATH - CLI will not be available globally");
    }

    return result;
  }

  private async Task<ValidationResult> ValidateIntegrationAsync()
  {
    var result = new ValidationResult(_bus);

    // Test message bus
    try
    {
      var testChannel = $"ghost:test:{Guid.NewGuid()}";
      await _bus.PublishAsync(testChannel, "test");

      var received = false;
      var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

      await foreach (var msg in _bus.SubscribeAsync<string>(testChannel, cts.Token))
      {
        if (msg == "test")
        {
          received = true;
          break;
        }
      }

      if (!received)
      {
        result.AddWarning(typeof(ValidateCommand), "Integration",
            "Message bus subscription test failed");
      }
    }
    catch (Exception ex)
    {
      result.AddWarning(typeof(ValidateCommand), "Integration",
          $"Message bus test failed: {ex.Message}");
    }

    return result;
  }

  private async Task FixInstallationIssuesAsync(ValidationResult results)
  {
    // Re-run installer with --repair flag
    if (OperatingSystem.IsWindows())
    {
      var psi = new ProcessStartInfo
      {
          FileName = "ghost",
          Arguments = "install --repair",
          UseShellExecute = true,
          Verb = "runas" // Request elevation
      };

      try
      {
        var process = Process.Start(psi);
        if (process != null)
        {
          await process.WaitForExitAsync();
          AnsiConsole.MarkupLine(process.ExitCode == 0
              ? "[green]Repair completed successfully[/]"
              : "[red]Repair failed[/]");
        }
      }
      catch (Exception ex)
      {
        AnsiConsole.MarkupLine($"[red]Repair failed:[/] {ex.Message}");
      }
    } else
    {
      // For Linux/macOS, use sudo
      var psi = new ProcessStartInfo
      {
          FileName = "sudo",
          Arguments = "ghost install --repair",
          UseShellExecute = false
      };

      try
      {
        var process = Process.Start(psi);
        if (process != null)
        {
          await process.WaitForExitAsync();
          AnsiConsole.MarkupLine(process.ExitCode == 0
              ? "[green]Repair completed successfully[/]"
              : "[red]Repair failed[/]");
        }
      }
      catch (Exception ex)
      {
        AnsiConsole.MarkupLine($"[red]Repair failed:[/] {ex.Message}");
      }
    }
  }

  private async Task<(bool installed, string status)> CheckServiceStatusAsync()
  {
    if (OperatingSystem.IsWindows())
    {
      var psi = new ProcessStartInfo
      {
          FileName = "sc",
          Arguments = "query GhostFatherDaemon",
          RedirectStandardOutput = true,
          UseShellExecute = false
      };

      try
      {
        var process = Process.Start(psi);
        if (process == null) return (false, "Unknown");

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0) return (false, "Not installed");

        if (output.Contains("RUNNING")) return (true, "Running");
        if (output.Contains("STOPPED")) return (true, "Stopped");
        if (output.Contains("PAUSED")) return (true, "Paused");

        return (true, "Unknown");
      }
      catch
      {
        return (false, "Error");
      }
    } else
    {
      var psi = new ProcessStartInfo
      {
          FileName = "systemctl",
          Arguments = "is-active ghost",
          RedirectStandardOutput = true,
          UseShellExecute = false
      };

      try
      {
        var process = Process.Start(psi);
        if (process == null) return (false, "Unknown");

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0
            ? (true, output.Trim())
            : (false, "Not installed");
      }
      catch
      {
        return (false, "Error");
      }
    }
  }

  private static string GetInstallRoot()
  {
    if (OperatingSystem.IsWindows())
    {
      return Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
          "Ghost");
    }

    return "/usr/local/ghost";
  }
}
