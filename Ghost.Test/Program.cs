using Ghost;

// Running the app
var app = new MyApp();
await app.StartAsync(args);


// Using attribute configuration
[GhostApp(IsService = false, AutoMonitor = true)]
public class MyApp : GhostApp
{
  public MyApp() : base(null)
  {
    // Register for state changes and errors
    StateChanged += (sender, state) => Console.WriteLine($"App state: {state}");
    ErrorOccurred += (sender, ex) => Console.WriteLine($"Error: {ex.Message}");
  }

  public override async Task StartAsync(IEnumerable<string> args)
  {
    G.LogInfo("My app is running!");
    // Application logic here
  }
}

public class MyService : GhostServiceApp
{
  public MyService()
  {
    // Register for state changes and errors
    StateChanged += (sender, state) => Console.WriteLine($"Service state: {state}");
    ErrorOccurred += (sender, ex) => Console.WriteLine($"Error: {ex.Message}");
  }

  protected override async Task ServiceTickAsync()
  {
    // Periodic service logic here
    await G.TrackMetricAsync("service.health", 100);
  }
}
