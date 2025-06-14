using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
namespace Ghost.Templates;

public class TemplateInfo
{

    private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            ContractResolver = new CamelCasePropertyNamesContractResolver()
    };
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
    public Dictionary<string, string> Variables { get; set; } = new Dictionary<string, string>();

    [JsonProperty("requiredPackages")]
    public Dictionary<string, string> RequiredPackages { get; set; } = new Dictionary<string, string>();

    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = new List<string>();

    public void LoadTemplateInfo(string? templateRoot = null)
    {
        string? aux = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        TemplateRoot = templateRoot ?? aux;

        string? infoPath = Path.Combine(TemplateRoot, "template.json");
        if (File.Exists(infoPath))
        {
            try
            {
                string? json = File.ReadAllText(infoPath);
                TemplateInfo? info = JsonConvert.DeserializeObject<TemplateInfo>(json, Settings);

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

                G.LogDebug($"Loaded template info: {Name} ({Description})");
            }
            catch (JsonException ex)
            {
                G.LogError(ex, $"Invalid JSON in template file: {infoPath}");
                throw new InvalidOperationException($"Invalid template configuration: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                G.LogError(ex, $"Failed to load template info: {infoPath}");
                throw;
            }
        }
        else
        {
            G.LogWarn($"Template info file not found: {infoPath}");
        }
    }

    public async Task<bool> ValidateEnvironmentAsync()
    {
        foreach (var package in RequiredPackages)
        {
            if (!await DotNetHelper.IsPackageInstalledAsync(package.Key))
            {
                return false;
            }
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

        string? infoPath = Path.Combine(TemplateRoot, "template.json");
        string? json = JsonConvert.SerializeObject(this, Settings);
        File.WriteAllText(infoPath, json);
    }
}
