using Ghost.Core.Config;
using Ghost.Father.CLI;
using Ghost.Father.Daemon;

namespace Ghost;

public static partial class GhostFather
{
    public static async Task Run(string[] args)
    {
        // Determine if we're running in daemon mode
        bool isDaemon = args.Contains("--daemon");
        if (isDaemon)
        {
            await RunDaemon(args);
        }
        else
        {
            await RunCli(args);
        }
    }

    private static async Task RunDaemon(string[] args)
    {
        try
        {
            // Create config from args
            var config = CreateConfigFromArgs(args);

            // Create and run daemon
            var daemon = new GhostFatherDaemon(config);
            await daemon.RunAsync(args);

            // Wait for shutdown signal
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await Task.Delay(-1, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            finally
            {
                await daemon.StopAsync();
            }
        }
        catch (Exception ex)
        {
            L.LogCritical("Fatal error in GhostFatherDaemon", ex);
            Environment.Exit(1);
        }
    }

    private static async Task RunCli(string[] args)
    {
        // Create CLI config
        var config = new GhostConfig
        {
            App = new AppInfo
            {
                Id = "ghost-cli",
                Name = "Ghost CLI",
                Description = "Ghost Command Line Interface",
                Version = "1.0.0"
            },
            Core = new CoreConfig
            {
                HealthCheckInterval = TimeSpan.FromSeconds(30),
                MetricsInterval = TimeSpan.FromSeconds(5)
            }
        };

        // Create and run CLI
        var cli = new GhostFatherCLI(config);
        await cli.RunAsync(args); //TODO: pass args and config
    }

    private static GhostConfig CreateConfigFromArgs(string[] args)
    {
        var config = new GhostConfig
        {
            App = new AppInfo
            {
                Id = "ghost-daemon",
                Name = "GhostFather Daemon",
                Description = "Ghost Process Manager Daemon",
                Version = "1.0.0"
            },
            Core = new CoreConfig
            {
                HealthCheckInterval = TimeSpan.FromSeconds(30),
                MetricsInterval = TimeSpan.FromSeconds(5)
            },
        };

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int port))
                    {
                        config.Core.ListenPort = port;
                        i++;
                    }
                    break;

                case "--data-dir":
                    if (i + 1 < args.Length)
                    {
                        var cachePath = args[i + 1];
                        config.Modules["cache"] = new LocalCacheConfig
                        {
                            Enabled = true,
                            Path = cachePath
                        };
                        i++;
                    }
                    break;

                case "--use-redis":
                    if (i + 1 < args.Length)
                    {
                        config.Modules["redis"] = new RedisConfig
                        {
                            Enabled = true,
                            ConnectionString = args[i + 1]
                        };
                        i++;
                    }
                    break;

                case "--use-postgres":
                    if (i + 1 < args.Length)
                    {
                        config.Modules["postgres"] = new PostgresConfig
                        {
                            Enabled = true,
                            ConnectionString = args[i + 1]
                        };
                        i++;
                    }
                    break;

                case "--production":
                    config.Core.Mode = "production";
                    break;
            }
        }

        // Apply defaults if not specified
        if (!config.Modules.ContainsKey("cache"))
        {
            config.Modules["cache"] = new LocalCacheConfig
            {
                Enabled = true,
                Path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Ghost"
                )
            };
        }

        if (!config.Modules.ContainsKey("logging"))
        {
            config.Modules["logging"] = new LoggingConfig
            {
                Enabled = true,
                LogsPath = "logs",
                OutputsPath = "outputs"
            };
        }

        return config;
    }
}