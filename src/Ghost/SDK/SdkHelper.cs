using System.IO;
using System.Diagnostics;
using Ghost.Core;

namespace Ghost.Father.CLI;

/// <summary>
/// Helper class for managing the Ghost SDK
/// </summary>
public static class SdkHelper
{
    /// <summary>
    /// Gets the local NuGet feed path for Ghost packages
    /// </summary>
    public static string GetLocalFeedPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "packages");
    }

    /// <summary>
    /// Gets the NuGet package output directory
    /// </summary>
    public static string GetNuGetOutputPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "nupkg");
    }

    /// <summary>
    /// Gets the path to the source files for the SDK
    /// </summary>
    public static string GetSdkSourcePath()
    {
        // Try to find the SDK source files in various locations
        var baseDir = AppContext.BaseDirectory;

        // 1. Check in development path
        var devPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "src", "Ghost", "SDK"));
        if (Directory.Exists(devPath))
            return devPath;

        // 2. Check in installed tool path
        var installPath = Path.Combine(baseDir, "src", "Ghost", "SDK");
        if (Directory.Exists(installPath))
            return installPath;

        // 3. Check in current assembly location
        var assemblyPath = Path.GetDirectoryName(typeof(SdkHelper).Assembly.Location);
        var assemblyAdjacentPath = Path.Combine(assemblyPath, "src", "Ghost", "SDK");
        if (Directory.Exists(assemblyAdjacentPath))
            return assemblyAdjacentPath;

        // 4. Fallback to embedded resources
        return null;
    }

    /// <summary>
    /// Checks if a specific package version exists in the local feed
    /// </summary>
    public static bool PackageExists(string packageId, string version)
    {
        var feedPath = GetLocalFeedPath();
        if (!Directory.Exists(feedPath))
            return false;

        return File.Exists(Path.Combine(feedPath, $"{packageId}.{version}.nupkg"));
    }

    /// <summary>
    /// Restores NuGet packages for a project
    /// </summary>
    public static async Task<bool> RestorePackagesAsync(string projectPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "restore",
                WorkingDirectory = projectPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process == null)
                return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to restore packages for project: {0}", projectPath);
            return false;
        }
    }

    /// <summary>
    /// Creates an empty SDK assembly that can be used as a placeholder
    /// when the full SDK build process fails
    /// </summary>
    public static async Task CreateEmptySdkAssemblyAsync(string outputDir, string version)
    {
        // Create directory for the assembly
        Directory.CreateDirectory(outputDir);

        // Create minimal source file
        var sourceCode = @"
namespace Ghost.SDK
{
    public static class GhostApp
    {
        public static void Execute(string[] args)
        {
            System.Console.WriteLine(""Ghost SDK placeholder"");
        }
    }
}";
        var sourcePath = Path.Combine(outputDir, "GhostApp.cs");
        await File.WriteAllTextAsync(sourcePath, sourceCode);

        // Create project file
        var projectFile = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>{version}</Version>
    <AssemblyName>Ghost.SDK</AssemblyName>
    <RootNamespace>Ghost.SDK</RootNamespace>
    <PackageId>Ghost.SDK</PackageId>
    <Authors>Ghost Team</Authors>
    <Description>Ghost SDK (Placeholder)</Description>
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
  </PropertyGroup>
</Project>";
        var projectPath = Path.Combine(outputDir, "Ghost.SDK.csproj");
        await File.WriteAllTextAsync(projectPath, projectFile);

        // Create nuspec file
        var nuspecFile = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<package xmlns=""http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"">
  <metadata>
    <id>Ghost.SDK</id>
    <version>{version}</version>
    <authors>Ghost Team</authors>
    <description>Ghost SDK (Placeholder)</description>
    <dependencies>
      <group targetFramework=""net9.0"" />
    </dependencies>
  </metadata>
</package>";
        var nuspecPath = Path.Combine(outputDir, "Ghost.SDK.nuspec");
        await File.WriteAllTextAsync(nuspecPath, nuspecFile);
        
        // Build the assembly
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build -c Release",
                WorkingDirectory = outputDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
            }
        }
        catch (Exception ex)
        {
            G.LogError(ex, "Failed to build empty SDK assembly");
        }
    }
}