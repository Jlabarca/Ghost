namespace Ghost.Modules;

public abstract class ModuleConfig
{
    public bool Enabled { get; set; }
    public string Provider { get; set; }
    public Dictionary<string, string> Options { get; set; }
        = new Dictionary<string, string>();

    public virtual IEnumerable<string> Validate()
    {
        if (!Enabled)
        {
            yield break;
        }

        if (string.IsNullOrEmpty(Provider))
        {
            yield return $"Provider must be specified for {GetType().Name}";
        }
    }
}
public interface IGhostModule
{
    string Name { get; }
    bool IsEnabled { get; }
    Task InitializeAsync();
    Task StartAsync();
    Task StopAsync();
    IEnumerable<string> ValidateConfiguration();
}
