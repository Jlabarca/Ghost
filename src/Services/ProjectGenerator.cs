using Ghost.Infrastructure;
namespace Ghost.Services;

public class ProjectGenerator  // Remove abstract
{
  private readonly ProcessRunner _processRunner;
  private readonly string _templatePath;

  // Single public constructor with dependency
  public ProjectGenerator(ProcessRunner processRunner)
  {
    _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    _templatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ghost", "templates");
  }

  public void CreateProject(string name)
  {
    // Rest of the implementation stays the same
    Directory.CreateDirectory(name);

    var template = ProjectTemplate.CreateDefault(name);
    foreach (var (path, content) in template.Files)
    {
      var fullPath = Path.Combine(name, path);
      var directory = Path.GetDirectoryName(fullPath);

      if (!string.IsNullOrEmpty(directory))
      {
        Directory.CreateDirectory(directory);
      }

      File.WriteAllText(fullPath, content);
    }

    var result = _processRunner.RunProcess("git", new[] { "init" }, name);
    if (result != null && result.ExitCode != 0)
    {
      throw new GhostException("Failed to initialize git repository ExitCode:" + result.ExitCode);
    }

    File.WriteAllText(
        Path.Combine(name, ".gitignore"),
        """
        ## .NET Core
        bin/
        obj/

        ## Visual Studio
        .vs/
        *.user
        """);
  }
}
