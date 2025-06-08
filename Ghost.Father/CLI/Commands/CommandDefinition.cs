namespace Ghost.Father.CLI;

/// <summary>
///     Command metadata and registration information
/// </summary>
public class CommandDefinition
{

    public CommandDefinition(Type commandType, string name, string description, params string[] examples)
    {
        CommandType = commandType;
        Name = name;
        Description = description;
        Examples = examples;
    }
    public Type CommandType { get; }
    public string Name { get; }
    public string Description { get; }
    public string[] Examples { get; }
}
