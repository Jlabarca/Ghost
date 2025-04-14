using Newtonsoft.Json;
using System.Reflection;

namespace Ghost.Templates;

public class TemplateInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("description")]
    public string Description { get; set; } = "";

    [JsonProperty("author")]
    public string Author { get; set; } = "";

    [JsonProperty("version")]
    public string Version { get; set; } = "";

    [JsonIgnore]
    public string TemplateRoot { get; set; } = "";

    [JsonProperty("variables")]
    public Dictionary<string, string> Variables { get; set; } = new();

    [JsonProperty("requiredPackages")]
    public Dictionary<string, string> RequiredPackages { get; set; } = new();

    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = new();

    private static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
    };

    public void LoadTemplateInfo(string? templateRoot = null)
    {
        var aux = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        TemplateRoot = templateRoot ?? aux;

        var infoPath = Path.Combine(TemplateRoot, "template.json");
        if (File.Exists(infoPath))
        {
            try
            {
                var json = File.ReadAllText(infoPath);
                var info = JsonConvert.DeserializeObject<TemplateInfo>(json, Settings);

                if (info == null)
                {
                    throw new InvalidOperationException($"Failed to deserialize template info from: {infoPath}");
                }

                // Copy properties
                Name = info.Name;
                Description = info.Description;
                Author = info.Author;
                Version = info.Version;
                Variables = info.Variables;
                RequiredPackages = info.RequiredPackages;
                Tags = info.Tags;

                L.LogDebug($"Loaded template info: {Name} ({Description})");
            }
            catch (JsonException ex)
            {
                L.LogError(ex, $"Invalid JSON in template file: {infoPath}");
                throw new InvalidOperationException($"Invalid template configuration: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                L.LogError(ex, $"Failed to load template info: {infoPath}");
                throw;
            }
        }
        else
        {
            L.LogWarn($"Template info file not found: {infoPath}");
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

    // Helper method to save template info
    public void SaveTemplateInfo()
    {
        if (string.IsNullOrEmpty(TemplateRoot))
        {
            throw new InvalidOperationException("TemplateRoot not set");
        }

        var infoPath = Path.Combine(TemplateRoot, "template.json");
        var json = JsonConvert.SerializeObject(this, Settings);
        File.WriteAllText(infoPath, json);
    }
}