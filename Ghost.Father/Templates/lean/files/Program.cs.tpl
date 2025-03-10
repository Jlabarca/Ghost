using Ghost.SDK;

namespace {{ defaultNamespace }};

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            using var app = new App();
            await app.ExecuteAsync(args);
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
    public override async Task RunAsync(IEnumerable<string> args)
    {
        G.LogInfo("Hello from {{ safe_name }}!");
        await Task.CompletedTask;
    }
}