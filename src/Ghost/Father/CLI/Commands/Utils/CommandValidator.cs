using Ghost.Core.Storage;
using Ghost.Father.CLI.Commands;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Reflection;

namespace Ghost.Father.CLI;

public class CommandValidator
{
  private readonly IServiceCollection _services;
  private readonly IGhostBus _bus;
  private readonly IDictionary<string, Type> _registeredCommands;

  public CommandValidator(IServiceCollection services, IGhostBus bus)
  {
    _services = services;
    _bus = bus;
    _registeredCommands = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
  }

  public void RegisterCommand<TCommand>(string name) where TCommand : class, ICommand
  {
    _registeredCommands[name] = typeof(TCommand);
  }

  public ValidationResult ValidateCommands()
  {
    var result = new ValidationResult(_bus);

    foreach (var (commandName, commandType) in _registeredCommands)
    {
      try
      {
        ValidateCommand(commandType, commandName, result);
      }
      catch (Exception ex)
      {
        result.AddError(commandType, commandName, $"Unexpected error validating command: {ex.Message}");
      }
    }

    return result;
  }

  private void ValidateCommand(Type commandType, string commandName, ValidationResult result)
  {
    // Check service registration
    var serviceDescriptor = _services.FirstOrDefault(s => s.ServiceType == commandType);
    if (serviceDescriptor == null)
    {
      result.AddError(commandType, commandName,
          $"Command '{commandName}' ({commandType.Name}) is not registered in DI container. " +
          $"Add 'services.AddTransient<{commandType.Name}>();' to your service configuration.");
      return;
    }

    // Get constructor with dependencies
    var constructor = commandType.GetConstructors()
        .OrderByDescending(c => c.GetParameters().Length)
        .FirstOrDefault();

    if (constructor == null)
    {
      result.AddError(commandType, commandName,
          $"Command '{commandName}' ({commandType.Name}) has no public constructor");
      return;
    }

    // Validate constructor parameters
    foreach (var parameter in constructor.GetParameters())
    {
      ValidateParameter(parameter, commandType, commandName, result);
    }

    // Validate settings if present
    ValidateSettings(commandType, commandName, result);
  }

  private void ValidateParameter(ParameterInfo parameter, Type commandType, string commandName, ValidationResult result)
  {
    var parameterType = parameter.ParameterType;
    var serviceDescriptor = _services.FirstOrDefault(s => s.ServiceType == parameterType);

    if (serviceDescriptor == null)
    {
      var suggestions = GetPotentialFixes(parameterType);
      result.AddMissingDependency(
          commandType,
          commandName,
          parameterType,
          $"Parameter '{parameter.Name}' of type '{parameterType.Name}' is not registered in DI container.\n" +
          $"Potential fixes:\n{string.Join("\n", suggestions)}");
    }
  }

  private void ValidateSettings(Type commandType, string commandName, ValidationResult result)
  {
    var settingsProperty = commandType.GetProperty("Settings");
    if (settingsProperty == null) return;

    var settingsType = settingsProperty.PropertyType;
    if (!typeof(CommandSettings).IsAssignableFrom(settingsType))
    {
      result.AddError(commandType, commandName,
          $"Settings type '{settingsType.Name}' must inherit from CommandSettings");
      return;
    }

    // Validate setting properties
    foreach (var property in settingsType.GetProperties())
    {
      var commandArgAttr = property.GetCustomAttribute<CommandArgumentAttribute>();
      var commandOptAttr = property.GetCustomAttribute<CommandOptionAttribute>();

      if (commandArgAttr == null && commandOptAttr == null)
      {
        result.AddWarning(commandType, commandName,
            $"Property '{property.Name}' in settings is not decorated with CommandArgument or CommandOption attribute");
      }
    }
  }

  private IEnumerable<string> GetPotentialFixes(Type missingType)
  {
    yield return $"services.AddTransient<{missingType.Name}>();";
    yield return $"services.AddScoped<{missingType.Name}>();";
    yield return $"services.AddSingleton<{missingType.Name}>();";

    if (missingType.IsInterface)
    {
      var implementations = Assembly.GetExecutingAssembly().GetTypes()
          .Where(t => !t.IsAbstract && missingType.IsAssignableFrom(t))
          .Take(3); // Limit suggestions

      foreach (var impl in implementations)
      {
        yield return $"services.AddTransient<{missingType.Name}, {impl.Name}>();";
      }
    }
  }
}
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
public enum IssueType
{
  Error,
  Warning,
  MissingDependency
}
public class ValidationIssue
{
  public IssueType Type { get; }
  public Type CommandType { get; }
  public string CommandName { get; }
  public string Message { get; }
  public Type DependencyType { get; init; }

  public ValidationIssue(IssueType type, Type commandType, string commandName, string message)
  {
    Type = type;
    CommandType = commandType;
    CommandName = commandName;
    Message = message;
  }
}
