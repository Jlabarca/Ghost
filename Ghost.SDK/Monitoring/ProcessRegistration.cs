using MemoryPack;

namespace Ghost
{
    [MemoryPackable]
    public partial class ProcessRegistration
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public string Version { get; set; }
        public string ExecutablePath { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public Dictionary<string, string> Environment { get; set; }
        public Dictionary<string, string> Configuration { get; set; }

        public ProcessRegistration()
        {
        }

        [MemoryPackConstructor]
        public ProcessRegistration(
                string id,
                string name,
                string type,
                string version,
                string executablePath,
                string arguments = "",
                string workingDirectory = null,
                Dictionary<string, string> environment = null,
                Dictionary<string, string> configuration = null)
        {
            Id = id;
            Name = name;
            Type = type;
            Version = version;
            ExecutablePath = executablePath;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            Environment = environment;
            Configuration = configuration;
        }

        /// <summary>
        /// Whether the process should automatically restart if it exits
        /// </summary>
        [MemoryPackIgnore]
        public bool AutoRestart => Configuration.TryGetValue("autoRestart", out var value) && bool.TryParse(value, out var result) && result;

        /// <summary>
        /// Maximum number of restart attempts (0 = unlimited)
        /// </summary>
        [MemoryPackIgnore]
        public int MaxRestartAttempts => Configuration.TryGetValue("maxRestartAttempts", out var value) && int.TryParse(value, out var result) ? result : 3;

        /// <summary>
        /// Delay in milliseconds before restarting the process
        /// </summary>
        [MemoryPackIgnore]
        public int RestartDelayMs => Configuration.TryGetValue("restartDelayMs", out var value) && int.TryParse(value, out var result) ? result : 5000;

        /// <summary>
        /// Whether to watch for file changes and restart the process
        /// </summary>
        [MemoryPackIgnore]
        public bool Watch => Configuration.TryGetValue("watch", out var value) && bool.TryParse(value, out var result) && result;

        /// <summary>
        /// Whether this process is a long-running service (vs a one-shot application)
        /// </summary>
        [MemoryPackIgnore]
        public bool IsService => Configuration.TryGetValue("AppType", out var appType) && string.Equals(appType, "service", StringComparison.OrdinalIgnoreCase);



        /// <summary>
        /// Get the full command line for the process
        /// </summary>
        public string GetCommandLine()
        {
            return string.IsNullOrEmpty(Arguments) ? ExecutablePath : $"{ExecutablePath} {Arguments}";
        }

        /// <summary>
        /// Clone the registration with a new ID
        /// </summary>
        public ProcessRegistration Clone(string newId = null)
        {
            var clone = new ProcessRegistration
            {
                Id = newId ?? Id,
                Name = Name,
                Type = Type,
                Version = Version,
                ExecutablePath = ExecutablePath,
                Arguments = Arguments,
                WorkingDirectory = WorkingDirectory,
                Environment = new Dictionary<string, string>(Environment),
                Configuration = new Dictionary<string, string>(Configuration)
            };
            return clone;
        }
    }
}