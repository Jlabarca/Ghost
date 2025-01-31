using System.Security.AccessControl;
using System.Security.Principal;
using Ghost.Legacy.Infrastructure;

namespace Ghost.Legacy.Services;

public class WorkspaceManager
{
    private readonly ConfigManager _configManager;
    private readonly string _workspacePath;
    private readonly int _maxApps;
    private readonly int _cleanupAgeDays;

    public WorkspaceManager(ConfigManager configManager)
    {
        _configManager = configManager;

        // Get workspace settings from config
        WorkspaceSettings settings = _configManager.GetWorkspaceSettings();
        _workspacePath = settings.Path;
        _maxApps = settings.MaxApps;
        _cleanupAgeDays = settings.CleanupAge;

        // Ensure workspace exists with proper permissions
        EnsureWorkspaceExists();
    }

    private void EnsureWorkspaceExists()
    {
        if (!Directory.Exists(_workspacePath))
        {
            Directory.CreateDirectory(_workspacePath);
            SetDirectoryPermissions(_workspacePath);
        }
    }

    private void SetDirectoryPermissions(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dirInfo = new DirectoryInfo(path);
                var security = dirInfo.GetAccessControl();

                // Grant full control to the current user
                var userIdentity = WindowsIdentity.GetCurrent();
                var fileSystemRule = new FileSystemAccessRule(
                        userIdentity.Name,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow);

                security.AddAccessRule(fileSystemRule);
                dirInfo.SetAccessControl(security);
            }
            else
            {
                // For Unix systems, use chmod 700 (read/write/execute for owner only)
                var process = new ProcessRunner(new GhostLogger());
                process.RunProcess("chmod", new[] { "700", path });
            }
        }
        catch (Exception ex)
        {
            throw new GhostException(
                    $"Failed to set workspace permissions: {ex.Message}",
                    ErrorCode.ProcessError);
        }
    }

    public string GetAppPath(string appName, string instanceId)
    {
        var sanitizedName = SanitizeName(appName);
        return Path.Combine(_workspacePath, $"{sanitizedName}_{instanceId}");
    }

    public void CleanApp(string appName)
    {
        var appDirs = Directory.GetDirectories(_workspacePath, $"{SanitizeName(appName)}_*");
        foreach (var dir in appDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                throw new GhostException(
                        $"Failed to clean app {appName}: {ex.Message}",
                        ErrorCode.ProcessError);
            }
        }
    }

    public int CleanAllApps()
    {
        var dirs = Directory.GetDirectories(_workspacePath);
        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception)
            {
                // Continue with other directories if one fails
                continue;
            }
        }
        return dirs.Length;
    }

    public int CleanOlderThan(TimeSpan age)
    {
        var threshold = DateTime.Now - age;
        var count = 0;

        var dirs = Directory.GetDirectories(_workspacePath);
        foreach (var dir in dirs)
        {
            try
            {
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.LastAccessTime < threshold)
                {
                    Directory.Delete(dir, recursive: true);
                    count++;
                }
            }
            catch (Exception)
            {
                // Continue with other directories if one fails
                continue;
            }
        }

        return count;
    }

    public void EnforceWorkspaceLimits()
    {
        var dirs = Directory.GetDirectories(_workspacePath)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastAccessTime)
                .ToList();

        // Remove oldest directories if we're over the limit
        if (dirs.Count > _maxApps)
        {
            foreach (var dir in dirs.Skip(_maxApps))
            {
                try
                {
                    dir.Delete(recursive: true);
                }
                catch
                {
                    // Best effort cleanup
                    continue;
                }
            }
        }

        // Clean up old apps
        CleanOlderThan(TimeSpan.FromDays(_cleanupAgeDays));
    }

    private string SanitizeName(string name)
    {
        // Remove or replace invalid characters
        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
    }
}

public class WorkspaceSettings
{
    public string Path { get; set; }
    public int MaxApps { get; set; }
    public int CleanupAge { get; set; }
}