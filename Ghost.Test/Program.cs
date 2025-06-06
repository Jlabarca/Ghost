using Ghost;

new MyApp().Execute(args);

// Using attribute configuration
[GhostApp(IsService = false, AutoMonitor = true)]
public class MyApp : GhostApp
{
  public MyApp()
  {
    // Register for state changes and errors
    StateChanged += (sender, state) => G.LogInfo($"App state: {state}");
    ErrorOccurred += (sender, ex) => G.LogInfo(($"Error: {ex.Message}"));
  }

  // protected override void ConfigureServices(IServiceCollection services)
  // {
  //
  // }

  public override Task RunAsync(IEnumerable<string> args)
  {
    //var config = Services.GetService<GhostConfig>();
    G.LogInfo("My app is running!");
    return Task.CompletedTask;
  }
}

//old  GhostServiceApp now GhostApp has a
// public class MyService : GhostServiceApp
// {
//   public MyService()
//   {
//     // Register for state changes and errors
//     StateChanged += (sender, state) => Console.WriteLine($"Service state: {state}");
//     ErrorOccurred += (sender, ex) => Console.WriteLine($"Error: {ex.Message}");
//   }
//
//   protected override async Task ServiceTickAsync()
//   {
//     // Periodic service logic here
//     await G.TrackMetricAsync("service.health", 100);
//   }
// }
