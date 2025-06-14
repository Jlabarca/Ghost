using System.Reflection;
using Ghost.Storage;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
namespace Ghost.Father.CLI;

public class CommandValidator
{
    private readonly IGhostBus _bus;
    private readonly IDictionary<string, Type> _registeredCommands;
    private readonly IServiceCollection _services;

    public CommandValidator(IServiceCollection services, IGhostBus bus)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _registeredCommands = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
    }

    public void RegisterCommand<TCommand>(string name) where TCommand : class, ICommand
    {
        _registeredCommands[name] = typeof(TCommand);
    }

    public ValidationResult ValidateCommands()
    {
        ValidationResult? result = new ValidationResult(_bus);
        foreach ((string? commandName, Type? commandType) in _registeredCommands)
        {
            try
            {
                ValidateCommand(commandType, commandName, result);

                // Special validations for specific commands
                if (commandName == "run")
                {
                    ValidateRunCommand(commandType, result);
                }
                else if (commandName == "monitor")
                {
                    ValidateMonitorCommand(commandType, result);
                }
                else if (commandName == "register")
                {
                    ValidateRegisterCommand(commandType, result);
                }
            }
            catch (Exception ex)
            {
                result.AddError(commandType, commandName, $"Unexpected error validating command: {ex.Message}");
            }
        }
        return result;
    }

    private void ValidateRegisterCommand(Type commandType, ValidationResult result)
    {
        // Check if RegisterCommand has the required dependencies
        var requiredTypes = new[]
        {
                typeof(IGhostBus)
                //typeof(IStorageProvider)
        };

        foreach (Type? requiredType in requiredTypes)
        {
            if (!HasDependency(commandType, requiredType))
            {
                result.AddMissingDependency(
                        commandType,
                        "register",
                        requiredType,
                        $"RegisterCommand requires {requiredType.Name} dependency.");
            }
        }

        // Check settings properties
        Type? settingsType = GetSettingsType(commandType);
        if (settingsType != null)
        {
            string[]? requiredSettings = new[]
            {
                    "Name", "Args",
                    "Watch", "Environment",
                    "Background"
            };

            foreach (string? setting in requiredSettings)
            {
                if (!HasProperty(settingsType, setting))
                {
                    result.AddWarning(
                            commandType,
                            "register",
                            $"RegisterCommand.Settings is missing property: {setting}");
                }
            }
        }
    }

    // Add specialized validator for RunCommand
    private void ValidateRunCommand(Type commandType, ValidationResult result)
    {
        // Check if RunCommand has the required dependencies
        var requiredTypes = new[]
        {
                typeof(IGhostBus)
        };

        foreach (Type? requiredType in requiredTypes)
        {
            if (!HasDependency(commandType, requiredType))
            {
                result.AddMissingDependency(
                        commandType,
                        "run",
                        requiredType,
                        $"RunCommand requires {requiredType.Name} dependency.");
            }
        }

        // Check settings properties
        Type? settingsType = GetSettingsType(commandType);
        if (settingsType != null)
        {
            string[]? requiredSettings = new[]
            {
                    "Name", "Args",
                    "Watch", "Environment",
                    "Background"
            };

            foreach (string? setting in requiredSettings)
            {
                if (!HasProperty(settingsType, setting))
                {
                    result.AddWarning(
                            commandType,
                            "run",
                            $"RunCommand.Settings is missing property: {setting}");
                }
            }
        }
    }

    // Add specialized validator for MonitorCommand
    private void ValidateMonitorCommand(Type commandType, ValidationResult result)
    {
        // Check if MonitorCommand has the required dependencies
        var requiredTypes = new[]
        {
                typeof(IGhostBus)
        };

        foreach (Type? requiredType in requiredTypes)
        {
            if (!HasDependency(commandType, requiredType))
            {
                result.AddMissingDependency(
                        commandType,
                        "monitor",
                        requiredType,
                        $"MonitorCommand requires {requiredType.Name} dependency.");
            }
        }

        // Check for necessary handling methods
        string[]? requiredMethods = new[]
        {
                "FetchInitialProcessList", "MonitorProcessMetricsAsync",
                "MonitorHealthStatusAsync", "UpdateProcessTables"
        };

        foreach (string? method in requiredMethods)
        {
            if (!HasMethod(commandType, method))
            {
                result.AddWarning(
                        commandType,
                        "monitor",
                        $"MonitorCommand is missing method: {method}");
            }
        }
    }

    // Helper methods
    private bool HasDependency(Type type, Type dependencyType)
    {
        return type.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == dependencyType || dependencyType.IsAssignableFrom(p.ParameterType));
    }

    private Type GetSettingsType(Type commandType)
    {
        return commandType.BaseType?.GenericTypeArguments.FirstOrDefault();
    }

    private bool HasProperty(Type type, string propertyName)
    {
        return type.GetProperty(propertyName) != null;
    }

    private bool HasMethod(Type type, string methodName)
    {
        return type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .Any(m => m.Name == methodName);
    }
    private void ValidateCommand(Type commandType, string commandName, ValidationResult result)
    {
        // Check service registration
        ServiceDescriptor? serviceDescriptor = _services.FirstOrDefault(s => s.ServiceType == commandType);
        if (serviceDescriptor == null)
        {
            result.AddError(
                    commandType,
                    commandName,
                    $"Command '{commandName}' ({commandType.Name}) is not registered in DI container. " +
                    $"Add 'services.AddTransient<{commandType.Name}>();' to your service configuration.");
            return;
        }

        // Get constructor with dependencies
        ConstructorInfo? constructor = commandType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

        if (constructor == null)
        {
            result.AddError(
                    commandType,
                    commandName,
                    $"Command '{commandName}' ({commandType.Name}) has no public constructor");
            return;
        }

        // Validate constructor parameters
        foreach (ParameterInfo? parameter in constructor.GetParameters())
        {
            ValidateParameter(parameter, commandType, commandName, result);
        }

        // Validate settings if present
        ValidateSettings(commandType, commandName, result);
    }

    private void ValidateParameter(ParameterInfo parameter, Type commandType, string commandName, ValidationResult result)
    {
        Type? parameterType = parameter.ParameterType;
        ServiceDescriptor? serviceDescriptor = _services.FirstOrDefault(s => s.ServiceType == parameterType);

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

    private IEnumerable<string> GetPotentialFixes(Type missingType)
    {
        if (missingType == null)
        {
            yield break;
        }

        yield return $"services.AddTransient<{missingType.Name}>();";
        yield return $"services.AddScoped<{missingType.Name}>();";
        yield return $"services.AddSingleton<{missingType.Name}>();";

        if (missingType.IsInterface)
        {
            var implementations = Assembly.GetExecutingAssembly().GetTypes()
                    .Where(t => t != null && !t.IsAbstract && missingType.IsAssignableFrom(t))
                    .Take(3); // Limit suggestions

            foreach (Type? impl in implementations)
            {
                yield return $"services.AddTransient<{missingType.Name}, {impl.Name}>();";
            }
        }
    }

    private void ValidateSettings(Type commandType, string commandName, ValidationResult result)
    {
        PropertyInfo? settingsProperty = commandType.GetProperty("Settings");
        if (settingsProperty == null)
        {
            return;
        }

        Type? settingsType = settingsProperty.PropertyType;
        if (!typeof(CommandSettings).IsAssignableFrom(settingsType))
        {
            result.AddError(
                    commandType,
                    commandName,
                    $"Settings type '{settingsType.Name}' must inherit from CommandSettings");
            return;
        }

        // Validate setting properties
        foreach (PropertyInfo? property in settingsType.GetProperties())
        {
            CommandArgumentAttribute? commandArgAttr = property.GetCustomAttribute<CommandArgumentAttribute>();
            CommandOptionAttribute? commandOptAttr = property.GetCustomAttribute<CommandOptionAttribute>();

            if (commandArgAttr == null && commandOptAttr == null)
            {
                result.AddWarning(
                        commandType,
                        commandName,
                        $"Property '{property.Name}' in settings is not decorated with CommandArgument or CommandOption attribute");
            }
        }
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

    public ValidationIssue(IssueType type, Type commandType, string commandName, string message)
    {
        Type = type;
        CommandType = commandType;
        CommandName = commandName;
        Message = message;
    }
    public IssueType Type { get; }
    public Type CommandType { get; }
    public string CommandName { get; }
    public string Message { get; }
    public Type DependencyType { get; init; }
}
public class ValidationResult
{
    private readonly IGhostBus _bus;
    private readonly List<ValidationIssue> _issues = new List<ValidationIssue>();

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

    public IEnumerable<ValidationIssue> GetIssues()
    {
        return _issues.OrderBy(i => i.Type);
    }

    public int GetIssueCount()
    {
        return _issues.Count;
    }

    public static ValidationResult Error(string message)
    {
        ValidationResult? result = new ValidationResult(null);
        result.AddError(typeof(object), "General", message);
        return result;
    }

    public static ValidationResult Success()
    {
        return new ValidationResult(null);
    }
}
