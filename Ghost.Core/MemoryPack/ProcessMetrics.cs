using System.Diagnostics;
using MemoryPack;
namespace Ghost;

[MemoryPackable]
public partial record ProcessMetrics(
        string ProcessId,
        double CpuPercentage,
        long MemoryBytes,
        int ThreadCount,
        DateTime Timestamp,
        long NetworkInBytes = 0,
        long NetworkOutBytes = 0,
        long DiskReadBytes = 0,
        long DiskWriteBytes = 0,
        int HandleCount = 0,
        long GcTotalMemory = 0,
        long Gen0Collections = 0,
        long Gen1Collections = 0,
        long Gen2Collections = 0)
{
    [MemoryPackIgnore]
    public double MemoryPercentage
    {
        get
        {
            // Note: Environment.SystemPageSize is typically 4096 or similar,
            // not the total system memory. This calculation gives a value
            // related to memory pages, not % of total RAM.
            // If you need % of total RAM, you'd need to get total system memory separately.
            try
            {
                // Added safety check for division by zero
                if (Environment.SystemPageSize > 0 && MemoryBytes > 0)
                {
                    // Original calculation logic:
                    return MemoryBytes / (double)Environment.SystemPageSize * 100;
                }
            }
            catch
            { /* Handle potential exceptions if necessary */
            }
            return 0;
        }
    }


    // Create a metrics snapshot from the current process
    // This static method remains unchanged as it's not directly related to serialization format.
    public static ProcessMetrics CreateSnapshot(string processId)
    {
        Process? process = Process.GetCurrentProcess();

        // Note: Getting accurate CPU%, Network, and Disk bytes typically requires
        // performance counters or platform-specific APIs and monitoring over time.
        // This snapshot provides basic info available cross-platform easily.

        return new ProcessMetrics(
                processId,
                0, // Snapshot CPU% is generally not meaningful
                process.WorkingSet64,
                process.Threads.Count,
                DateTime.UtcNow,
                0, // Placeholder - Requires advanced monitoring
                0, // Placeholder - Requires advanced monitoring
                0, // process.ReadOperationCount is count, not bytes. Placeholder.
                0, // process.WriteOperationCount is count, not bytes. Placeholder.
                process.HandleCount,
                GC.GetTotalMemory(false), // Approx memory managed by GC
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2)
        );
    }
}
