// using Ghost.Data;
// using Ghost.Monitoring;
// using Microsoft.Extensions.DependencyInjection;
// using Microsoft.Extensions.Logging;
//
// namespace Ghost.Testing.InMemory
// {
//     /// <summary>
//     /// Extensions for setting up Ghost services for testing.
//     /// </summary>
//     public static class GhostTestingExtensions
//     {
//         /// <summary>
//         /// Adds Ghost data services with in-memory implementations for testing.
//         /// </summary>
//         /// <param name="services">The service collection.</param>
//         /// <returns>The service collection for chaining.</returns>
//         public static IServiceCollection AddGhostDataForTesting(this IServiceCollection services)
//         {
//             if (services == null)
//                 throw new ArgumentNullException(nameof(services));
//
//             // Register the in-memory data store
//             services.AddSingleton<IGhostData, InMemoryGhostData>();
//
//             // Register test metrics collector
//             services.AddSingleton<IMetricsCollector, InMemoryMetricsCollector>();
//
//             return services;
//         }
//
//         /// <summary>
//         /// Adds Ghost data services with decorator chain for testing.
//         /// </summary>
//         /// <param name="services">The service collection.</param>
//         /// <param name="useInstrumentationLayer">Whether to use the instrumentation layer.</param>
//         /// <param name="useResilienceLayer">Whether to use the resilience layer.</param>
//         /// <param name="useCachingLayer">Whether to use the caching layer.</param>
//         /// <param name="useEncryptionLayer">Whether to use the encryption layer.</param>
//         /// <returns>The service collection for chaining.</returns>
//         public static IServiceCollection AddGhostDataLayersForTesting(
//             this IServiceCollection services,
//             bool useInstrumentationLayer = true,
//             bool useResilienceLayer = true,
//             bool useCachingLayer = true,
//             bool useEncryptionLayer = false)
//         {
//             if (services == null)
//                 throw new ArgumentNullException(nameof(services));
//
//             // Register in-memory services
//             services.AddSingleton<InMemoryGhostData>();
//             services.AddSingleton<IMetricsCollector, InMemoryMetricsCollector>();
//             //services.AddMemoryCache();
//
//             // Base layer
//             services.AddSingleton<IGhostData>(sp =>
//             {
//                 IGhostData data = sp.GetRequiredService<InMemoryGhostData>();
//
//                 // Add encryption layer
//                 if (useEncryptionLayer)
//                 {
//                     data = new EncryptedGhostData(
//                         data,
//                         Microsoft.Extensions.Options.Options.Create(new Configuration.SecurityConfiguration
//                         {
//                             EnableEncryption = true,
//                             EncryptionKey = Convert.ToBase64String(new byte[32] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 }),
//                             EncryptionIV = Convert.ToBase64String(new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })
//                         }),
//                         sp.GetRequiredService<ILogger<EncryptedGhostData>>());
//                 }
//
//                 // Add caching layer
//                 if (useCachingLayer)
//                 {
//                     data = new CachedGhostData(
//                         data,
//                         sp.GetRequiredService<ICache>(),
//                         Microsoft.Extensions.Options.Options.Create(new Configuration.CachingConfiguration
//                         {
//                             UseL1Cache = true,
//                             DefaultL1Expiration = TimeSpan.FromMinutes(5),
//                             DefaultL1SlidingExpiration = TimeSpan.FromMinutes(1)
//                         }),
//                         sp.GetRequiredService<ILogger<CachedGhostData>>());
//                 }
//
//                 // Add resilience layer
//                 if (useResilienceLayer)
//                 {
//                     data = new Data.Decorators.ResilientGhostData(
//                         data,
//                         sp.GetRequiredService<ILogger<Data.Decorators.ResilientGhostData>>(),
//                         Microsoft.Extensions.Options.Options.Create(new Configuration.ResilienceConfiguration
//                         {
//                             EnableRetry = true,
//                             RetryCount = 3,
//                             RetryBaseDelayMs = 100,
//                             EnableCircuitBreaker = true,
//                             CircuitBreakerThreshold = 5,
//                             CircuitBreakerDurationMs = 30000
//                         }));
//                 }
//
//                 // Add instrumentation layer
//                 if (useInstrumentationLayer)
//                 {
//                     data = new InstrumentedGhostData(
//                         data,
//                         sp.GetRequiredService<IMetricsCollector>(),
//                         sp.GetRequiredService<ILogger<InstrumentedGhostData>>());
//                 }
//
//                 return data;
//             });
//
//             return services;
//         }
//     }
//
// }

