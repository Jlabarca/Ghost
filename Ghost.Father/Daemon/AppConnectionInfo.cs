namespace Ghost.Father.Daemon;

public class AppConnectionInfo
{
  public string Id { get; set; }
  public ProcessMetadata Metadata { get; set; }
  public string Status { get; set; }
  public string LastMessage { get; set; }
  public DateTime LastSeen { get; set; }
  public ProcessMetrics LastMetrics { get; set; } // Ensure ProcessMetrics is defined and accessible
  public bool IsDaemon { get; set; } = false;
}