using Ghost.Father.CLI.Commands;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Ghost.Father.CLI;

/// <summary>
/// Command metadata and registration information
/// </summary>
public class CommandDefinition
{
    public Type CommandType { get; }
    public string Name { get; }
    public string Description { get; }
    public string[] Examples { get; }

    public CommandDefinition(Type commandType, string name, string description, params string[] examples)
    {
        CommandType = commandType;
        Name = name;
        Description = description;
        Examples = examples;
    }
}

/// <summary>
/// Central registry for Ghost CLI commands
/// </summary>
public static class CommandRegistry
{
    private static readonly List<CommandDefinition> _commands = new()
    {
        new(typeof(CreateCommand), "create", "Create a new Ghost app project",
            "create myapp", "create myapp --template service"),

        new(typeof(RunCommand), "run", "Run a Ghost app",
            "run myapp", "run myapp --watch"),

        new(typeof(InstallCommand), "install", "Install GhostFatherDaemon as a system service",
            "install", "install --user"),

        new(typeof(PushCommand), "push", "Create git repo and push current Ghost app",
            "push", "push --remote origin"),

        new(typeof(PullCommand), "pull", "Pull Ghost app from repo and optionally run it",
            "pull https://github.com/user/repo.git"),

        new(typeof(MonitorCommand), "monitor", "Monitor running Ghost apps",
            "monitor", "monitor --watch"),

        new(typeof(RemoveCommand), "remove", "Remove a Ghost app",
            "remove myapp"),
            
        new(typeof(ValidateCommand), "validate", "Validate Ghost installation and configuration",
            "validate", "validate --verbose", "validate --fix")
    };

    /// <summary>
    /// Register all commands with the service collection
    /// </summary>
    public static void RegisterServices(IServiceCollection services)
    {
        foreach (var command in _commands)
        {
            services.AddTransient(command.CommandType);
        }
    }

    /// <summary>
    /// Configure all commands in the Spectre.Console CLI
    /// </summary>
    public static void ConfigureCommands(IConfigurator config)
    {
        foreach (var command in _commands)
        {
            var method = typeof(IConfigurator)
                .GetMethod("AddCommand")?
                .MakeGenericMethod(command.CommandType);

            var commandConfig = method?.Invoke(config, new object[] { command.Name }) as ICommandConfigurator;
            
            if (commandConfig != null)
            {
                commandConfig.WithDescription(command.Description);
                foreach (var example in command.Examples)
                {
                    commandConfig.WithExample(example.Split(' '));
                }
            }
        }
    }

    /// <summary>
    /// Register all commands with the validator
    /// </summary>
    public static void RegisterWithValidator(CommandValidator validator)
    {
        foreach (var command in _commands)
        {
            var method = typeof(CommandValidator)
                .GetMethod("RegisterCommand")?
                .MakeGenericMethod(command.CommandType);

            method?.Invoke(validator, new object[] { command.Name });
        }
    }

    /// <summary>
    /// Get all registered commands
    /// </summary>
    public static IEnumerable<CommandDefinition> GetCommands() => _commands;

    /// <summary>
    /// Add a new command definition to the registry
    /// </summary>
    public static void AddCommand(CommandDefinition command)
    {
        _commands.Add(command);
    }
}