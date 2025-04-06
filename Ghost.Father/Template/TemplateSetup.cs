namespace Ghost.Templates;

public static class TemplateSetup
{
    public static void EnsureTemplatesExist(string baseDirectory)
    {
        var templatesPath = Path.Combine(baseDirectory, "templates");
        Directory.CreateDirectory(templatesPath);

        // Create lean template
        CreateLeanTemplate(templatesPath);
        
        // Create default template
        CreateDefaultTemplate(templatesPath);
    }

    private static void CreateLeanTemplate(string templatesPath)
    {
        var leanPath = Path.Combine(templatesPath, "lean");
        var leanFilesPath = Path.Combine(leanPath, "files");
        
        Directory.CreateDirectory(leanPath);
        Directory.CreateDirectory(leanFilesPath);

        // Create template.json
        var templateJson = """
        {
            "name": "lean",
            "description": "Minimal Ghost app with core features only",
            "author": "Ghost",
            "version": "1.0.0",
            "variables": {
                "defaultNamespace": "{{ safe_name }}",
                "defaultDescription": "A Ghost application"
            },
            "requiredPackages": {
                "Ghost.Core": "1.0.0"
            },
            "tags": ["basic", "lean"]
        }
        """;
        File.WriteAllText(Path.Combine(leanPath, "template.json"), templateJson);

        // Create Program.cs template
        var programCs = """
        using Ghost.SDK;

        namespace {{ defaultNamespace }};

        public class Program 
        {
            public static async Task<int> Main(string[] args)
            {
                try 
                {
                    using var app = new App();
                    await app.StartAsync();
                    
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                    
                    await app.StopAsync();
                    return 0;
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync($"Fatal error: {ex.Message}");
                    return 1;
                }
            }
        }

        public class App : GhostApp 
        {
            public override async Task RunAsync()
            {
                G.LogInfo("Hello from {{ safe_name }}!");
                await Task.CompletedTask;
            }
        }
        """;
        File.WriteAllText(Path.Combine(leanFilesPath, "Program.cs.tpl"), programCs);

        // Create project file template
        var csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
            <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AssemblyName>{{ safe_name }}</AssemblyName>
                <RootNamespace>{{ defaultNamespace }}</RootNamespace>
            </PropertyGroup>

            <ItemGroup>
                <PackageReference Include="Ghost.SDK" Version="1.0.0" />
            </ItemGroup>
        </Project>
        """;
        File.WriteAllText(Path.Combine(leanFilesPath, "{{ project_name }}.csproj.tpl"), csproj);

        // Create .ghost.yaml template
        var ghostConfig = """
        app:
          id: "{{ safe_name }}"
          name: "{{ project_name }}"
          description: "{{ defaultDescription }}"
          version: "1.0.0"

        core:
          healthCheckInterval: "00:00:30"
          metricsInterval: "00:00:05"
          dataDirectory: "data"

        modules:
          logging:
            enabled: true
            provider: "file"
            options:
              path: "logs"
              level: "Information"
        """;
        File.WriteAllText(Path.Combine(leanFilesPath, ".ghost.yaml.tpl"), ghostConfig);
    }

    private static void CreateDefaultTemplate(string templatesPath)
    {
        var defaultPath = Path.Combine(templatesPath, "default");
        var defaultFilesPath = Path.Combine(defaultPath, "files");
        
        Directory.CreateDirectory(defaultPath);
        Directory.CreateDirectory(defaultFilesPath);

        // Create template.json
        var templateJson = """
        {
            "name": "default",
            "description": "Standard Ghost application template with full features",
            "author": "Ghost",
            "version": "1.0.0",
            "variables": {
                "defaultNamespace": "{{ safe_name }}",
                "defaultDescription": "A Ghost application"
            },
            "requiredPackages": {
                "Ghost.SDK": "1.0.0",
                "Ghost.Core": "1.0.0"
            },
            "tags": ["default", "standard"]
        }
        """;
        File.WriteAllText(Path.Combine(defaultPath, "template.json"), templateJson);

        // Add the same files as lean but with more features
        // (Would add more robust implementations here)
        CreateLeanTemplate(templatesPath);
    }
}