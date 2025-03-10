using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Diagnostics;

namespace Ghost.Father.CLI.Commands;

public class UpdateSdkCommand : AsyncCommand<UpdateSdkCommand.Settings>
{
  public class Settings : CommandSettings
  {
    [CommandOption("--version")]
    [Description("Version number for the SDK package")]
    public string Version { get; set; } = "1.0.0";

    [CommandOption("--output")]
    [Description("Output directory for SDK packages")]
    public string OutputDirectory { get; set; } = "nupkg";

    [CommandOption("--local-feed")]
    [Description("Local NuGet feed path")]
    public string LocalFeed { get; set; }
  }

  public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
  {
    try
    {
      await AnsiConsole.Status()
          .StartAsync("Building Ghost SDK packages...", async ctx =>
          {
            // 1) Determine your final output folder -- same folder as ghost.exe
            var ghostExeDirectory = AppContext.BaseDirectory;
            var outputDir = Path.GetFullPath(Path.Combine(ghostExeDirectory, "nupkg"));

            // Create or clean the directory
            Directory.CreateDirectory(outputDir);

            // 2) Extract SDK sources into outputDir (unchanged)
            ctx.Status("Extracting SDK sources...");
            await ExtractSdkSourcesAsync(outputDir);

            // 3) Generate .csproj with <GeneratePackageOnBuild> etc. (unchanged)
            ctx.Status("Creating SDK project files...");
            await CreateSdkProjectFilesAsync(outputDir, settings.Version);

            // 4) Actually build + pack into the ghost.exe folder
            ctx.Status("Building & packing the SDK...");
            bool success = await BuildAndPackSdkAsync(outputDir, ghostExeDirectory);
            if (!success)
            {
              AnsiConsole.MarkupLine("[red]Failed to build SDK packages[/]");
              return 1;
            }

            // 5) If you have a local feed, copy nupkgs over (unchanged)
            if (!string.IsNullOrEmpty(settings.LocalFeed))
            {
              ctx.Status("Installing packages to local feed...");
              await InstallToLocalFeedAsync(outputDir, settings.LocalFeed);
            }

            return 0;
          });

      return 0;
    }
    catch (Exception ex)
    {
      AnsiConsole.MarkupLine($"[red]Error building SDK:[/] {ex.Message}");
      return 1;
    }
  }

// This method changes a bit to accept the final ghostExeDirectory
  private async Task<bool> BuildAndPackSdkAsync(string outputDir, string ghostExeDirectory)
  {
    // Build + pack Ghost.Core first
    bool coreSuccess = await RunDotNetCommandAsync(
        "pack",
        // the -o arg points to the ghost.exe folder
        $"-c Release -o \"{ghostExeDirectory}\"",
        Path.Combine(outputDir, "Ghost.Core")
    );
    if (!coreSuccess) return false;

    // Then build + pack Ghost.SDK
    bool sdkSuccess = await RunDotNetCommandAsync(
        "pack",
        // again direct the output to ghost.exe folder
        $"-c Release -o \"{ghostExeDirectory}\"",
        Path.Combine(outputDir, "Ghost.SDK")
    );

    return sdkSuccess;
  }


  private async Task ExtractSdkSourcesAsync(string outputDir)
  {
    // Create directories for the packages
    var sdkDir = Path.Combine(outputDir, "Ghost.SDK");
    var coreDir = Path.Combine(outputDir, "Ghost.Core");

    Directory.CreateDirectory(sdkDir);
    Directory.CreateDirectory(coreDir);

    try
    {
      // Get the source directory for extraction
      var sourceDir = AppContext.BaseDirectory;
      var srcDir = Path.GetFullPath(Path.Combine(sourceDir, "..", "..", "..", "src", "Ghost"));

      // If we're in development, use the source directory directly
      if (Directory.Exists(srcDir) && Directory.Exists(Path.Combine(srcDir, "SDK")))
      {
        G.LogInfo($"Using development source directory: {srcDir}");

        // Copy SDK sources
        Directory.CreateDirectory(Path.Combine(sdkDir, "src"));
        CopyDirectory(Path.Combine(srcDir, "SDK"), Path.Combine(sdkDir, "src"), "*.cs");

        // Copy Core sources needed by SDK
        Directory.CreateDirectory(Path.Combine(coreDir, "src"));
        CopyDirectory(Path.Combine(srcDir, "Core"), Path.Combine(coreDir, "src"), "*.cs");
      } else
      {
        // Source directory not found, create from scratch
        G.LogInfo("Source directory not found. Creating SDK packages from scratch...");
        await CreateMinimalSdkImplementationAsync(sdkDir);
        await CreateMinimalCoreImplementationAsync(coreDir);
      }
    }
    catch (Exception ex)
    {
      G.LogError(ex, "Error extracting SDK sources. Creating from scratch instead.");
      await CreateMinimalSdkImplementationAsync(sdkDir);
      await CreateMinimalCoreImplementationAsync(coreDir);
    }
  }

  /// <summary>
  /// Creates a minimal implementation of the Ghost.Core package when source files aren't available
  /// </summary>
  private async Task CreateMinimalCoreImplementationAsync(string coreDir)
  {
    var srcDir = Path.Combine(coreDir, "src");
    Directory.CreateDirectory(srcDir);

    // Create the basic core exception and error codes
    var coreImplementation = @"using System;

namespace Ghost.Core
{
    public enum ErrorCode 
    { 
        Unknown, 
        ProcessError,
        StorageError,
        ConfigurationError,
        NetworkError,
        TemplateError,
        TemplateNotFound,
        GitError,
        InstallationError
    }
    
    public class GhostException : Exception
    {
        public ErrorCode Code { get; }
        
        public GhostException(string message) : base(message)
        {
            Code = ErrorCode.Unknown;
        }
        
        public GhostException(string message, Exception innerException) 
            : base(message, innerException)
        {
            Code = ErrorCode.Unknown;
        }
        
        public GhostException(string message, Exception innerException, ErrorCode code) 
            : base(message, innerException)
        {
            Code = code;
        }
        
        public GhostException(string message, ErrorCode code) : base(message)
        {
            Code = code;
        }
    }
}";

    await File.WriteAllTextAsync(Path.Combine(srcDir, "GhostCore.cs"), coreImplementation);

    // Create logging interface
    var loggingImplementation = @"using System;

namespace Ghost.Core.Logging
{
    public interface ILogger
    {
        void Log(string message, string level = ""INFO"", Exception ex = null);
    }
}";

    Directory.CreateDirectory(Path.Combine(srcDir, "Logging"));
    await File.WriteAllTextAsync(Path.Combine(srcDir, "Logging", "ILogger.cs"), loggingImplementation);

    // Create minimal config
    var configImplementation = @"using System.Collections.Generic;

namespace Ghost.Core.Config
{
    public class GhostConfig
    {
        public AppInfo App { get; set; }
        public CoreConfig Core { get; set; }
        public Dictionary<string, object> Modules { get; set; } = new Dictionary<string, object>();
    }
    
    public class AppInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
    }
    
    public class CoreConfig
    {
        public string LogsPath { get; set; } = ""logs"";
        public string DataPath { get; set; } = ""data"";
    }
}";

    Directory.CreateDirectory(Path.Combine(srcDir, "Config"));
    await File.WriteAllTextAsync(Path.Combine(srcDir, "Config", "GhostConfig.cs"), configImplementation);
  }

  /// <summary>
  /// Creates a minimal implementation of the Ghost.SDK package when source files aren't available
  /// </summary>
  private async Task CreateMinimalSdkImplementationAsync(string sdkDir)
  {
    var srcDir = Path.Combine(sdkDir, "src");
    Directory.CreateDirectory(srcDir);

    // Create GhostApp base class
    var sdkImplementation = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;

namespace Ghost.SDK
{
    /// <summary>
    /// Base class for Ghost applications
    /// </summary>
    public class GhostApp
    {
        /// <summary>
        /// Creates a new Ghost application
        /// </summary>
        public GhostApp()
        {
            G.LogInfo($""Creating {GetType().Name}"");
        }
        
        /// <summary>
        /// Main execution method for the application
        /// </summary>
        public virtual Task RunAsync(IEnumerable<string> args)
        {
            G.LogInfo(""Hello from Ghost SDK!"");
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Start the application
        /// </summary>
        public virtual Task StartAsync()
        {
            G.LogInfo(""Starting Ghost application..."");
            return Task.CompletedTask;
        }
        
        /// <summary>
        /// Stop the application
        /// </summary>
        public virtual Task StopAsync()
        {
            G.LogInfo(""Stopping Ghost application..."");
            return Task.CompletedTask;
        }
    }
}";

    await File.WriteAllTextAsync(Path.Combine(srcDir, "GhostApp.cs"), sdkImplementation);

    // Create G helper class
    var gImplementation = @"using System;

namespace Ghost
{
    /// <summary>
    /// Static helper class for Ghost applications
    /// </summary>
    public static class G
    {
        /// <summary>
        /// Log an informational message
        /// </summary>
        public static void LogInfo(string message)
        {
            Console.WriteLine($""[INFO] {message}"");
        }
        
        /// <summary>
        /// Log a debug message
        /// </summary>
        public static void LogDebug(string message)
        {
            Console.WriteLine($""[DEBUG] {message}"");
        }
        
        /// <summary>
        /// Log a warning message
        /// </summary>
        public static void LogWarn(string message)
        {
            Console.WriteLine($""[WARN] {message}"");
        }
        
        /// <summary>
        /// Log an error message
        /// </summary>
        public static void LogError(string message, Exception ex = null)
        {
            Console.WriteLine($""[ERROR] {message}"");
            if (ex != null)
            {
                Console.WriteLine(ex.ToString());
            }
        }
        
        /// <summary>
        /// Log a critical error message
        /// </summary>
        public static void LogCritical(string message, Exception ex = null)
        {
            Console.WriteLine($""[CRITICAL] {message}"");
            if (ex != null)
            {
                Console.WriteLine(ex.ToString());
            }
        }
        
        /// <summary>
        /// Set the current app instance
        /// </summary>
        public static void SetCurrent(object app)
        {
            // Implementation stub
        }
    }
}";

    await File.WriteAllTextAsync(Path.Combine(srcDir, "G.cs"), gImplementation);
  }

  private async Task CreateSdkProjectFilesAsync(string outputDir, string version)
  {
    // Create Ghost.Core.csproj
    var coreProject = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>{version}</Version>
    <Authors>Ghost Team</Authors>
    <Description>Core library for Ghost applications</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Microsoft.Extensions.Logging"" Version=""9.0.0"" />
    <PackageReference Include=""Microsoft.Extensions.DependencyInjection"" Version=""9.0.0"" />
  </ItemGroup>
</Project>";

    // Create Ghost.SDK.csproj
    var sdkProject = $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Version>{version}</Version>
    <Authors>Ghost Team</Authors>
    <Description>SDK for building Ghost applications</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Microsoft.Extensions.Logging"" Version=""9.0.0"" />
    <PackageReference Include=""Microsoft.Extensions.DependencyInjection"" Version=""9.0.0"" />
    <PackageReference Include=""Ghost.Core"" Version=""{version}"" />
  </ItemGroup>
</Project>";

    // Write project files
    await File.WriteAllTextAsync(Path.Combine(outputDir, "Ghost.Core", "Ghost.Core.csproj"), coreProject);
    await File.WriteAllTextAsync(Path.Combine(outputDir, "Ghost.SDK", "Ghost.SDK.csproj"), sdkProject);
  }

  private async Task<bool> BuildAndPackSdkAsync(string outputDir)
  {
    // First build and pack Core
    bool coreSuccess = await RunDotNetCommandAsync("pack",
        $"-c Release -o \"{outputDir}\"",
        Path.Combine(outputDir, "Ghost.Core"));

    if (!coreSuccess) return false;

    // Then build and pack SDK
    bool sdkSuccess = await RunDotNetCommandAsync("pack",
        $"-c Release -o \"{outputDir}\"",
        Path.Combine(outputDir, "Ghost.SDK"));

    return sdkSuccess;
  }

  private async Task InstallToLocalFeedAsync(string outputDir, string localFeed)
  {
    Directory.CreateDirectory(localFeed);

    foreach (var nupkg in Directory.GetFiles(outputDir, "*.nupkg"))
    {
      var destFile = Path.Combine(localFeed, Path.GetFileName(nupkg));
      File.Copy(nupkg, destFile, true);
    }

    await Task.CompletedTask;
  }

  private async Task<bool> RunDotNetCommandAsync(string command, string args, string workingDir)
  {
    try
    {
      var psi = new ProcessStartInfo
      {
          FileName = "dotnet",
          Arguments = $"{command} {args}",
          WorkingDirectory = workingDir,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true
      };

      var process = Process.Start(psi);
      if (process == null) return false;

      var output = await process.StandardOutput.ReadToEndAsync();
      var error = await process.StandardError.ReadToEndAsync();

      await process.WaitForExitAsync();

      if (process.ExitCode != 0)
      {
        G.LogError($"Command failed: dotnet {command} {args}");
        G.LogError($"Error: {error}");
        return false;
      }

      return true;
    }
    catch (Exception ex)
    {
      G.LogError(ex, $"Failed to run dotnet command: {command} {args}");
      return false;
    }
  }

  private static void CopyDirectory(string sourceDir, string destinationDir, string pattern = "*.*")
  {
    // Create the destination directory if it doesn't exist
    Directory.CreateDirectory(destinationDir);

    // Copy all files matching the pattern
    foreach (var file in Directory.GetFiles(sourceDir, pattern))
    {
      var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
      File.Copy(file, destFile, true);
    }

    // Copy all subdirectories recursively
    foreach (var directory in Directory.GetDirectories(sourceDir))
    {
      var dirName = Path.GetFileName(directory);
      CopyDirectory(directory, Path.Combine(destinationDir, dirName), pattern);
    }
  }
}
