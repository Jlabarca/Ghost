using Ghost.Core.Storage;
using Ghost.Father.CLI.Commands;
using Spectre.Console;
namespace Ghost.Father.CLI;

public class ValidationResult
{
  private IGhostBus _bus;
  private readonly List<ValidationIssue> _issues = new();

  public ValidationResult(IGhostBus bus)
  {
    _bus = bus;
  }

  public bool IsValid => !_issues.Any(i => i.Type == IssueType.Error || i.Type == IssueType.MissingDependency);

  public void AddError(Type commandType, string commandName, string message)
  {
    _issues.Add(new ValidationIssue(IssueType.Error, commandType, commandName, message));
  }

  public void AddWarning(Type commandType, string commandName, string message)
  {
    _issues.Add(new ValidationIssue(IssueType.Warning, commandType, commandName, message));
  }

  public void AddMissingDependency(Type commandType, string commandName, Type dependencyType, string message)
  {
    _issues.Add(new ValidationIssue(IssueType.MissingDependency, commandType, commandName, message)
    {
        DependencyType = dependencyType
    });
  }

  public void PrintResults()
  {
    if (IsValid && !_issues.Any())
    {
      AnsiConsole.MarkupLine("[green]All commands validated successfully[/]");
      return;
    }

    var table = new Table()
        .AddColumn("Command")
        .AddColumn("Issue")
        .AddColumn("Details")
        .Border(TableBorder.Rounded);

    foreach (var issue in _issues.OrderBy(i => i.Type))
    {
      var (color, prefix) = issue.Type switch
      {
          IssueType.Error => ("red", "Error"),
          IssueType.Warning => ("yellow", "Warning"),
          IssueType.MissingDependency => ("red", "Missing Dependency"),
          _ => ("white", "Info")
      };

      table.AddRow(
          $"{issue.CommandName}\n[grey]({issue.CommandType.Name})[/]",
          $"[{color}]{prefix}[/]",
          issue.Message.Replace("\n", "\n  ")
      );
    }

    AnsiConsole.WriteLine();
    AnsiConsole.Write(new Rule("[yellow]Command Validation Results[/]"));
    AnsiConsole.Write(table);
    AnsiConsole.WriteLine();

    if (!IsValid)
    {
      AnsiConsole.MarkupLine("[red]Command validation failed - fix the above issues before proceeding[/]");
    }
  }

  public int GetIssueCount()
  {
    return _issues.Count;
  }

  public IEnumerable<ValidationIssue> GetIssues()
  {
    return _issues.OrderBy(i => i.Type);
  }

// Enhanced service validation method
  private async Task<ValidationResult> ValidateInstallationAsync(ValidateCommand.Settings settings)
  {
    var result = new ValidationResult(_bus);
    var installRoot = GetInstallRoot();

    if (settings.Verbose)
    {
      G.LogInfo($"Checking installation at: {installRoot}");
    }

    // Check installation directories
    var requiredDirs = new[]
    {
        "logs", "data",
        "apps", "templates"
    };

    foreach (var dir in requiredDirs)
    {
      var path = Path.Combine(installRoot, dir);
      if (settings.Verbose)
      {
        G.LogInfo($"Checking directory: {dir}");
      }

      if (!Directory.Exists(path))
      {
        result.AddError(typeof(ValidateCommand), "Installation",
            $"Required directory missing: {dir}");
        if (settings.Verbose)
        {
          G.LogWarn($"Directory not found: {path}");
        }
      } else if (settings.Verbose)
      {
        G.LogInfo($"Directory exists: {dir}");
      }
    }

    // Check executables
    var ghostExe = OperatingSystem.IsWindows() ? "ghost.exe" : "ghost";
    var ghostdExe = OperatingSystem.IsWindows() ? "ghostd.exe" : "ghostd";

    if (settings.Verbose)
    {
      G.LogInfo("Checking executables...");
    }

    var ghostPath = Path.Combine(installRoot, "bin", ghostExe);
    if (!File.Exists(ghostPath))
    {
      result.AddError(typeof(ValidateCommand), "Installation",
          $"Ghost CLI executable missing: {ghostExe}");
      if (settings.Verbose)
      {
        G.LogWarn($"Executable not found: {ghostPath}");
      }
    } else if (settings.Verbose)
    {
      G.LogInfo($"Found CLI executable: {ghostExe}");
    }

    var daemonPath = Path.Combine(installRoot, "bin", ghostdExe);
    if (!File.Exists(daemonPath))
    {
      result.AddError(typeof(ValidateCommand), "Installation",
          $"Ghost daemon executable missing: {ghostdExe}");
      if (settings.Verbose)
      {
        G.LogWarn($"Executable not found: {daemonPath}");
      }
    } else if (settings.Verbose)
    {
      G.LogInfo($"Found daemon executable: {ghostdExe}");
    }

    // Check service installation
    if (settings.Verbose)
    {
      G.LogInfo("Checking service status...");
    }

    var (serviceInstalled, serviceStatus) = await CheckServiceStatusAsync();
    if (!serviceInstalled)
    {
      result.AddError(typeof(ValidateCommand), "Service",
          "Ghost service is not installed");
      if (settings.Verbose)
      {
        G.LogWarn("Service not found in system");
      }
    } else if (serviceStatus != "Running")
    {
      result.AddWarning(typeof(ValidateCommand), "Service",
          $"Ghost service is not running (current status: {serviceStatus})");
      if (settings.Verbose)
      {
        G.LogWarn($"Service status: {serviceStatus}");
      }
    } else if (settings.Verbose)
    {
      G.LogInfo("Service is running");
    }

    // Check Redis connection if configured
    if (settings.Verbose)
    {
      G.LogInfo("Checking Redis connection...");
    }

    try
    {
      var isAvailable = await _bus.IsAvailableAsync();
      if (!isAvailable)
      {
        result.AddWarning(typeof(ValidateCommand), "Redis",
            "Redis connection failed - distributed features will be unavailable");
        if (settings.Verbose)
        {
          G.LogWarn("Redis connection test failed");
        }
      } else if (settings.Verbose)
      {
        G.LogInfo("Redis connection successful");
      }
    }
    catch (Exception ex)
    {
      if (settings.Verbose)
      {
        G.LogWarn($"Redis error: {ex.Message}");
        result.AddWarning(typeof(ValidateCommand), "Redis",
            $"Redis error: {ex.Message}");
      }
    }

    return result;
  }
  private async Task<(bool serviceInstalled, string serviceStatus)> CheckServiceStatusAsync()
  {
    return (false, "Stopped");
  }

  private string GetInstallRoot()
  {
    var installRoot = Environment.GetEnvironmentVariable("GHOST_INSTALL");
    if (string.IsNullOrEmpty(installRoot))
    {
      throw new InvalidOperationException("GHOST_INSTALL environment variable not set");
    }

    return installRoot;
  }
}
