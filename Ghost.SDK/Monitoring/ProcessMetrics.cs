// using MessagePack;
// /// <summary>
// /// Annotate ProcessMetrics with MessagePack attributes
// /// </summary>
// [MessagePackObject]
// public record ProcessMetrics(
//         [property: Key(0)] string ProcessId,
//         [property: Key(1)] double CpuPercentage,
//         [property: Key(2)] long MemoryBytes,
//         [property: Key(3)] int ThreadCount,
//         [property: Key(4)] DateTime Timestamp,
//         [property: Key(5)] long NetworkInBytes = 0,
//         [property: Key(6)] long NetworkOutBytes = 0,
//         [property: Key(7)] long DiskReadBytes = 0,
//         [property: Key(8)] long DiskWriteBytes = 0,
//         [property: Key(9)] int HandleCount = 0,
//         [property: Key(10)] long GcTotalMemory = 0,
//         [property: Key(11)] long Gen0Collections = 0,
//         [property: Key(12)] long Gen1Collections = 0,
//         [property: Key(13)] long Gen2Collections = 0)
// {
//
// }
//
