using System.Text.Json;
namespace Ghost.Templates;

public class GhostTemplate
{
    public string Name { get; set; }
    public string Description { get; set; }
    public Dictionary<string, string> Variables { get; set; } = new();
    public Dictionary<string, string> RequiredPackages { get; set; } = new();
    public string TemplateRoot { get; }

    public GhostTemplate(string name, string templateRoot)
    {
        Name = name;
        TemplateRoot = templateRoot;
        LoadTemplateInfo();
    }

    private void LoadTemplateInfo()
    {
        var infoPath = Path.Combine(TemplateRoot, "template.json");
        if (File.Exists(infoPath))
        {
            var info = JsonSerializer.Deserialize<TemplateInfo>(
                File.ReadAllText(infoPath));
            
            Description = info.Description;
            Variables = info.Variables;
            RequiredPackages = info.RequiredPackages;
        }
    }

    public async Task<bool> ValidateEnvironmentAsync()
    {
        foreach (var package in RequiredPackages)
        {
            if (!await DotNetHelper.IsPackageInstalledAsync(package.Key))
                return false;
        }
        return true;
    }
}
