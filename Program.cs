using Ghost.Core;

namespace Ghost;

public class Program
{
  public static async Task<int> Main(string[] args)
  {
    try
    {
      // Skip the first argument if it's the executable path
      var processedArgs = ProcessArguments(args);
      await G.Run(processedArgs);
      return 0;
    }
    catch (Exception ex)
    {
      await Console.Error.WriteLineAsync($"Fatal error: {ex.Message}");
      return 1;
    }
  }

  private static string[] ProcessArguments(string[] args)
  {
    // If the first argument is a file path (like the DLL path), skip it
    if (args.Length > 0 && (args[0].EndsWith(".dll") || args[0].EndsWith(".exe")))
    {
      return args.Skip(1).ToArray();
    }
    return args;
  }
}
