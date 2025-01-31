namespace Ghost.Infrastructure.Monitoring;

/// <summary>
/// Represents a snapshot of process performance metrics at a specific point in time.
/// Think of this as a "vital signs monitor" for a running process - capturing CPU, memory,
/// and other key health indicators.
/// </summary>
///
public record ProcessMetrics
{
  /// <summary>
  /// Unique identifier of the process being monitored
  /// </summary>
  public string ProcessId { get; init; }

  /// <summary>
  /// CPU usage as a percentage (0-100).
  /// Like a "speedometer" showing how hard the CPU is working.
  /// </summary>
  public double CpuPercentage { get; init; }

  /// <summary>
  /// Memory usage in bytes.
  /// Like a "fuel gauge" showing how much RAM the process is using.
  /// </summary>
  public long MemoryBytes { get; init; }

  /// <summary>
  /// Number of active threads in the process.
  /// Like a count of "workers" currently handling tasks.
  /// </summary>
  public int ThreadCount { get; init; }

  /// <summary>
  /// When these metrics were collected
  /// </summary>
  public DateTime Timestamp { get; init; }

  /// <summary>
  /// Creates a new ProcessMetrics instance
  /// </summary>
  public ProcessMetrics(
      string processId,
      double cpuPercentage,
      long memoryBytes,
      int threadCount,
      DateTime timestamp)
  {
    ProcessId = processId ?? throw new ArgumentNullException(nameof(processId));
    CpuPercentage = cpuPercentage;
    MemoryBytes = memoryBytes;
    ThreadCount = threadCount;
    Timestamp = timestamp;
  }

  /// <summary>
  /// Creates a snapshot of the current process metrics
  /// </summary>
  public static ProcessMetrics CreateSnapshot(string processId)
  {
    var process = System.Diagnostics.Process.GetCurrentProcess();

    return new ProcessMetrics(
        processId,
        // Note: This is a point-in-time CPU measurement, for accurate CPU %
        // you need to compare CPU time between two measurements
        0,
        process.WorkingSet64,
        process.Threads.Count,
        DateTime.UtcNow
    );
  }

  /// <summary>
  /// Returns a human-readable summary of the metrics
  /// </summary>
  public override string ToString()
  {
    return $"Process {ProcessId} at {Timestamp:HH:mm:ss.fff}: " +
           $"CPU: {CpuPercentage:F1}%, " +
           $"Memory: {MemoryBytes / 1024 / 1024:F1} MB, " +
           $"Threads: {ThreadCount}";
  }
}
