// src/Infrastructure/Monitoring/ProcessMetrics.cs
using System.Text.Json.Serialization;

namespace Ghost.Infrastructure.Monitoring;

public record ProcessMetrics
{
    [JsonPropertyName("processId")]
    public string ProcessId { get; init; }

    [JsonPropertyName("cpuPercentage")]
    public double CpuPercentage { get; init; }

    [JsonPropertyName("memoryBytes")]
    public long MemoryBytes { get; init; }

    [JsonPropertyName("threadCount")]
    public int ThreadCount { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }
    
    [JsonPropertyName("networkInBytes")]
    public long NetworkInBytes { get; init; }
    
    [JsonPropertyName("networkOutBytes")]
    public long NetworkOutBytes { get; init; }
    
    [JsonPropertyName("diskReadBytes")]
    public long DiskReadBytes { get; init; }
    
    [JsonPropertyName("diskWriteBytes")]
    public long DiskWriteBytes { get; init; }
    
    [JsonPropertyName("handleCount")]
    public int HandleCount { get; init; }
    
    [JsonPropertyName("gcTotalMemory")] 
    public long GCTotalMemory { get; init; }
    
    [JsonPropertyName("gen0Collections")]
    public long Gen0Collections { get; init; }
    
    [JsonPropertyName("gen1Collections")]
    public long Gen1Collections { get; init; }
    
    [JsonPropertyName("gen2Collections")]
    public long Gen2Collections { get; init; }

    // Memory usage as a percentage of total system memory
    [JsonIgnore]
    public double MemoryPercentage => 
        MemoryBytes / (double)Environment.SystemPageSize * 100;

    public ProcessMetrics(
        string processId,
        double cpuPercentage,
        long memoryBytes,
        int threadCount,
        DateTime timestamp,
        long networkInBytes = 0,
        long networkOutBytes = 0,
        long diskReadBytes = 0,
        long diskWriteBytes = 0,
        int handleCount = 0,
        long gcTotalMemory = 0,
        long gen0Collections = 0,
        long gen1Collections = 0,
        long gen2Collections = 0)
    {
        ProcessId = processId;
        CpuPercentage = cpuPercentage;
        MemoryBytes = memoryBytes;
        ThreadCount = threadCount;
        Timestamp = timestamp;
        NetworkInBytes = networkInBytes;
        NetworkOutBytes = networkOutBytes;
        DiskReadBytes = diskReadBytes;
        DiskWriteBytes = diskWriteBytes;
        HandleCount = handleCount;
        GCTotalMemory = gcTotalMemory;
        Gen0Collections = gen0Collections;
        Gen1Collections = gen1Collections;
        Gen2Collections = gen2Collections;
    }

    // Create a metrics snapshot from the current process
    public static ProcessMetrics CreateSnapshot(string processId)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        
        return new ProcessMetrics(
            processId: processId,
            cpuPercentage: 0, // This needs to be calculated over time
            memoryBytes: process.WorkingSet64,
            threadCount: process.Threads.Count,
            timestamp: DateTime.UtcNow,
            networkInBytes: 0, // Needs performance counter access
            networkOutBytes: 0, // Needs performance counter access
            // diskReadBytes: process.ReadOperationCount,
            // diskWriteBytes: process.WriteOperationCount,
            handleCount: process.HandleCount,
            gcTotalMemory: GC.GetTotalMemory(false),
            gen0Collections: GC.CollectionCount(0),
            gen1Collections: GC.CollectionCount(1),
            gen2Collections: GC.CollectionCount(2)
        );
    }
}
