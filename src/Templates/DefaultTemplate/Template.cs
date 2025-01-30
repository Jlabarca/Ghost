namespace Ghost.Services;

public partial class ProjectTemplate
{
    public Dictionary<string, string> Files { get; } = new();

    public void AddFile(string path, string content)
    {
        Files[path] = content;
    }

    public static ProjectTemplate CreateDefault(string name)
    {
        var template = new ProjectTemplate();

        // Project file
        template.AddFile($"{name}.csproj", """
                                       <Project Sdk="Microsoft.NET.Sdk">
                                         <PropertyGroup>
                                           <OutputType>Exe</OutputType>
                                           <TargetFramework>net8.0</TargetFramework>
                                           <ImplicitUsings>enable</ImplicitUsings>
                                           <Nullable>enable</Nullable>
                                         </PropertyGroup>
                                         <ItemGroup>
                                           <PackageReference Include="Spectre.Console" Version="0.49.1" />
                                           <PackageReference Include="Spectre.Console.Cli" Version="0.49.1" />
                                         </ItemGroup>
                                       </Project>
                                       """);

        // Program file
        template.AddFile("Program.cs", $$"""
                                     using Spectre.Console;
                                     using Spectre.Console.Cli;

                                     public class Program
                                     {
                                         public static int Main(string[] args)
                                         {
                                             var app = new CommandApp();
                                             app.Configure(config =>
                                             {
                                                 config.AddCommand<HelloCommand>("hello")
                                                     .WithDescription("Says hello!")
                                                     .WithExample(new[] { "hello", "--name", "World" });
                                             });
                                             return app.Run(args);
                                         }
                                     }

                                     public class HelloCommand : Command<HelloCommand.Settings>
                                     {
                                         public class Settings : CommandSettings
                                         {
                                             [CommandOption("--name")]
                                             public string Name { get; set; } = "World";
                                         }
                                     
                                         public override int Execute(CommandContext context, Settings settings)
                                         {
                                             AnsiConsole.MarkupLine($"Hello [green]{settings.Name.EscapeMarkup()}[/]!");
                                             return 0;
                                         }
                                     }
                                     """);

        // README
        template.AddFile("README.md", $$"""
                                    # {{name}}

                                    A modern .NET CLI application with full CI/CD setup.

                                    ## Development

                                    Requirements:
                                    - .NET 8.0 SDK
                                    - Git

                                    ## Getting Started

                                    ```bash
                                    dotnet run -- hello --name YourName
                                    ```

                                    ## Build and Test

                                    ```bash
                                    dotnet build
                                    dotnet test
                                    ```
                                    """);

        return template;
    }
}