namespace Ghost.SDK.Monitoring;

/// <summary>
/// Represents a summary of process metrics over time
/// Think of this as a "health report" that aggregates all the vital signs
/// </summary>
public class ProcessMetricsSummary
{
  public DateTime StartTime { get; set; }
  public DateTime EndTime { get; set; }
  public double AverageCpu { get; set; }
  public long PeakMemory { get; set; }
  public double AverageThreads { get; set; }
  public int SampleCount { get; set; }

  public TimeSpan Duration => EndTime - StartTime;
}
