using Ghost.Core.Config;
using Ghost.SDK;

namespace Ghost.Father.Daemon
{
    public class GhostFatherDaemon : GhostApp
    {
        private readonly ProcessManager _processManager;
        private readonly HealthMonitor _healthMonitor;
        private readonly CommandProcessor _commandProcessor;
        private readonly StateManager _stateManager;

        public GhostFatherDaemon(GhostConfig? config = null) : base(config)
        {
            // Initialize components
            _processManager = new ProcessManager(Bus, Data, Config);
            _healthMonitor = new HealthMonitor(Bus);
            _commandProcessor = new CommandProcessor(Bus);
            _stateManager = new StateManager(Data);

            // Configure the daemon
            ConfigureDaemon();
        }
        {
            // Configure as a service
            IsService = true;
            AutoGhostFather = false; // Don't auto-connect to avoid circular connection

            // Initialize managers
            _processManager = new ProcessManager(Bus, Data, Config, _healthMonitor, _stateManager);
            _healthMonitor = new HealthMonitor(Bus);
            _commandProcessor = new CommandProcessor(Bus);
            _stateManager = new StateManager(Data);
        }

        public override async Task RunAsync(IEnumerable<string> args)
        {
            G.LogInfo("GhostFather starting...");

            try
            {
                // Initialize components
                await InitializeAsync();

                // Register itself for monitoring
                await _processManager.RegisterSelfAsync();

                // Start process manager
                await _processManager.InitializeAsync();

                // Start command processor
                _ = _commandProcessor.StartProcessingAsync(CancellationToken.None);

                // Register command handlers
                RegisterCommandHandlers();

                // Discover Ghost apps
                await _processManager.DiscoverGhostAppsAsync();

                G.LogInfo("GhostFather initialized and ready");
            }
            catch (Exception ex)
            {
                G.LogError("Failed to initialize GhostFather", ex);
                throw;
            }
        }

        protected override async Task OnTickAsync()
        {
            try
            {
                // Process periodic tasks
                await _healthMonitor.CheckHealthAsync();
                await _processManager.MaintenanceTickAsync();

                // Periodically persist state
                if (DateTime.Now.Second % 5 == 0)
                    await _stateManager.PersistStateAsync();
            }
            catch (Exception ex)
            {
                G.LogError("Error in GhostFather tick", ex);
                // Let base class handle restart if needed
                throw;
            }
        }

        protected override async Task OnBeforeRunAsync()
        {
            G.LogInfo("GhostFather preparing to start...");

            // Ensure required directories exist
            Directory.CreateDirectory(Config.GetLogsPath());
            Directory.CreateDirectory(Config.GetDataPath());
            Directory.CreateDirectory(Config.GetAppsPath());

            await base.OnBeforeRunAsync();
        }

        protected override async Task OnAfterRunAsync()
        {
            try
            {
                G.LogInfo("GhostFather shutting down...");

                // Stop all processes
                await _processManager.StopAllAsync();

                // Persist final state
                await _stateManager.PersistStateAsync();
            }
            catch (Exception ex)
            {
                G.LogError("Error during GhostFather shutdown", ex);
            }
            finally
            {
                await base.OnAfterRunAsync();
            }
        }

        private void RegisterCommandHandlers()
        {
            _commandProcessor.RegisterHandler("start", HandleStartCommandAsync);
            _commandProcessor.RegisterHandler("stop", HandleStopCommandAsync);
            _commandProcessor.RegisterHandler("restart", HandleRestartCommandAsync);
            _commandProcessor.RegisterHandler("status", HandleStatusCommandAsync);
            _commandProcessor.RegisterHandler("register", HandleRegisterCommandAsync);
            _commandProcessor.RegisterHandler("run", HandleRunCommandAsync);
            _commandProcessor.RegisterHandler("ping", HandlePingCommandAsync);
        }

        // Command handlers

        private async Task HandleRegisterCommandAsync(SystemCommand cmd)
        {
            try
            {
                if (!cmd.Parameters.TryGetValue("registration", out var registrationJson))
                {
                    throw new ArgumentException("Registration data is required");
                }

                var registration = JsonSerializer.Deserialize<ProcessRegistration>(registrationJson);
                var force = cmd.Parameters.TryGetValue("force", out var forceStr) &&
                            bool.TryParse(forceStr, out var forceBool) && forceBool;

                G.LogInfo($"Registering process: {registration.Name} ({registration.Id})");

                // Check if process already exists
                try
                {
                    var existingProcess = await _processManager.GetProcessAsync(registration.Id);

                    if (existingProcess != null && !force)
                    {
                        throw new GhostException($"Process {registration.Id} already exists", ErrorCode.ProcessError);
                    }
                    else if (existingProcess != null)
                    {
                        // Stop existing process if it's running
                        if (existingProcess.Status == ProcessStatus.Running)
                        {
                            await _processManager.StopProcessAsync(existingProcess.Id);
                        }
                    }
                }
                catch (GhostException ex) when (ex.Code == ErrorCode.ProcessError)
                {
                    // Process not found, continue with registration
                }

                // Register process
                await _processManager.RegisterProcessAsync(registration);

                // Send success response
                await SendCommandResponseAsync(cmd, true);
            }
            catch (Exception ex)
            {
                G.LogError(ex, "Failed to register process");
                await SendCommandResponseAsync(cmd, false, ex.Message);
            }
        }

        private async Task HandlePingCommandAsync(SystemCommand cmd)
        {
            await SendCommandResponseAsync(cmd, true, data: new
            {
                Status = "Running",
                Timestamp = DateTime.UtcNow,
                Version = Config.App?.Version ?? "1.0.0"
            });
        }

        // Command response helpers

        private async Task SendCommandResponseAsync(SystemCommand cmd, bool success, string error = null, object data = null)
        {
            try
            {
                var response = new CommandResponse
                {
                    CommandId = cmd.CommandId,
                    Success = success,
                    Error = error,
                    Data = data,
                    Timestamp = DateTime.UtcNow
                };

                var responseChannel = cmd.Parameters.GetValueOrDefault("responseChannel", "ghost:responses");
                await Bus.PublishAsync(responseChannel, response);
            }
            catch (Exception ex)
            {
                G.LogError("Failed to send command response", ex);
            }
        }
    }
}