using Ghost;
using Microsoft.Extensions.DependencyInjection;

// Running the app
new MyApp().Execute(args);
//await app.StartAsync(args);


// Using attribute configuration
[GhostApp(IsService = false, AutoMonitor = true)]
public class MyApp : GhostApp
{
  public MyApp()
  {
    // Register for state changes and errors
    StateChanged += (sender, state) => Console.WriteLine($"App state: {state}");
    ErrorOccurred += (sender, ex) => Console.WriteLine($"Error: {ex.Message}");
  }

  public override Task RunAsync(IEnumerable<string> args)
  {
    //var config = Services.GetService<GhostConfig>();
    G.LogInfo("My app is running!");
    return Task.CompletedTask;
  }
}

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
