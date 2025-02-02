
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Reflection;

//TODO: move file but think about it first
namespace Ghost.Infrastructure.Validation;

public class CommandValidator
{
    private readonly IServiceCollection _services;

    public CommandValidator(IServiceCollection services)
    {
        _services = services;
    }

    public ValidationResult ValidateCommands()
    {
        var result = new ValidationResult();

        // Find all command types in the assembly
        var commandTypes = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => !t.IsAbstract && t.IsClass &&
                (typeof(ICommand).IsAssignableFrom(t) || typeof(AsyncCommand).IsAssignableFrom(t)));

        foreach (var commandType in commandTypes)
        {
            try
            {
                // Check if command type is registered in DI
                ValidateCommandRegistration(commandType, result);

                // Validate command dependencies
                ValidateCommandDependencies(commandType, result);
            }
            catch (Exception ex)
            {
                result.AddError(commandType, $"Unexpected error validating command: {ex.Message}");
            }
        }

        return result;
    }

    private void ValidateCommandRegistration(Type commandType, ValidationResult result)
    {
        var descriptor = _services.FirstOrDefault(s => s.ServiceType == commandType);
        if (descriptor == null)
        {
            result.AddError(commandType, "Command is not registered in the service collection");
        }
    }

    private void ValidateCommandDependencies(Type commandType, ValidationResult result)
    {
        // Get constructor with most parameters (usually the one with dependencies)
        var constructor = commandType.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (constructor == null)
        {
            result.AddError(commandType, "No public constructor found");
            return;
        }

        foreach (var parameter in constructor.GetParameters())
        {
            var parameterType = parameter.ParameterType;
            var descriptor = _services.FirstOrDefault(s => s.ServiceType == parameterType);

            if (descriptor == null)
            {
                result.AddMissingDependency(commandType, parameterType);
            }
        }
    }
}

public class ValidationResult
{
    private readonly List<ValidationError> _errors = new();
    private readonly List<MissingDependency> _missingDependencies = new();

    public bool IsValid => !_errors.Any() && !_missingDependencies.Any();

    public void AddError(Type commandType, string message)
    {
        _errors.Add(new ValidationError(commandType, message));
    }

    public void AddMissingDependency(Type commandType, Type dependencyType)
    {
        _missingDependencies.Add(new MissingDependency(commandType, dependencyType));
    }

    public void PrintResults()
    {
        if (IsValid)
        {
            AnsiConsole.MarkupLine("[green]All commands validated successfully[/]");
            return;
        }

        var table = new Table()
            .AddColumn("Command")
            .AddColumn("Issue")
            .AddColumn("Details");

        foreach (var error in _errors)
        {
            table.AddRow(
                error.CommandType.Name,
                "[red]Error[/]",
                error.Message
            );
        }

        foreach (var missing in _missingDependencies)
        {
            table.AddRow(
                missing.CommandType.Name,
                "[yellow]Missing Dependency[/]",
                $"Required: {missing.DependencyType.Name}"
            );
        }

        AnsiConsole.MarkupLine("[red]Command validation failed[/]");
        AnsiConsole.Write(table);
    }
}

public record ValidationError(Type CommandType, string Message);
public record MissingDependency(Type CommandType, Type DependencyType);