using Ghost.Father.CLI.Commands;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace Ghost.Father.CLI;

/// <summary>
/// Central registry for Ghost CLI commands
/// </summary>
public static class CommandRegistry
{
    static CommandRegistry()
    {
        // Validate command implementations at startup
        foreach (var command in _commands)
        {
            ValidateCommandImplementation(command);
        }
    }

    private static void ValidateCommandImplementation(CommandDefinition command)
    {
        G.LogDebug($"Validating command: {command.Name} ({command.CommandType.Name})");
        G.LogDebug($"  Base type: {command.CommandType.BaseType?.Name ?? "none"}");
        G.LogDebug($"  Implements ICommand: {typeof(ICommand).IsAssignableFrom(command.CommandType)}");

        // Check inheritance
        if (!typeof(ICommand).IsAssignableFrom(command.CommandType) &&
            !typeof(AsyncCommand).IsAssignableFrom(command.CommandType))
        {
            throw new InvalidOperationException(
                $"Command {command.Name} ({command.CommandType.Name}) must inherit from Command<T> or AsyncCommand<T>");
        }

        // Check for settings type
        var settingsType = command.CommandType.BaseType?.GenericTypeArguments.FirstOrDefault();
        if (settingsType == null || !typeof(CommandSettings).IsAssignableFrom(settingsType))
        {
            throw new InvalidOperationException(
                $"Command {command.Name} ({command.CommandType.Name}) must have Settings that inherit from CommandSettings");
        }

        // Check for execute method
        var executeMethod = command.CommandType.GetMethod("Execute") ??
                            command.CommandType.GetMethod("ExecuteAsync");
        if (executeMethod == null)
        {
            throw new InvalidOperationException(
                $"Command {command.Name} ({command.CommandType.Name}) must implement Execute or ExecuteAsync");
        }
    }

    private static readonly List<CommandDefinition> _commands = new()
    {
        new(typeof(VersionCommand), "version", "Display version information", "version"),

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
        // new(typeof(ValidateCommand), "validate", "Validate Ghost installation and configuration",
        //     "validate", "validate --verbose", "validate --fix"),
        // new(typeof(UpdateSdkCommand), "updatesdk", "Build and deploy the Ghost SDK as NuGet packages",
        //         "updatesdk", "updatesdk --version 1.1.0", "updatesdk --local-feed ./packages")
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
            // Get base command type (either Command<T> or AsyncCommand<T>)
            Type baseCommandType = command.CommandType.BaseType;
            if (baseCommandType == null || !baseCommandType.IsGenericType)
            {
                throw new InvalidOperationException(
                    $"Command {command.Name} must inherit from Command<T> or AsyncCommand<T>");
            }

            // Get the settings type from the generic argument
            Type settingsType = baseCommandType.GetGenericArguments()[0];

            // Use GetGenericMethod helper to ensure constraint satisfaction
            var addCommandMethod = typeof(IConfigurator).GetMethod("AddCommand")
                ?.MakeGenericMethod(command.CommandType);

            if (addCommandMethod == null)
            {
                throw new InvalidOperationException(
                    $"Could not create AddCommand method for {command.Name}. " +
                    $"Ensure it implements ICommand and has proper CommandSettings.");
            }

            var commandConfig = addCommandMethod.Invoke(config, new object[] { command.Name }) as ICommandConfigurator;
            
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