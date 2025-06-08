namespace Ghost.Father;

[AttributeUsage(AttributeTargets.Assembly)]
public class BuildInfoAttribute : Attribute
{

    public BuildInfoAttribute(int buildNumber)
    {
        BuildNumber = buildNumber;
    }
    public int BuildNumber { get; }
}
