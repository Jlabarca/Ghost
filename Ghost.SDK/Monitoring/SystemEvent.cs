using Ghost.Father.Ghost.Core.Monitoring;
using System;
using System.Text.Json;

namespace Ghost.Core.Monitoring
{
    /// <summary>
    /// Represents a system event that can be published through the message bus
    /// for inter-process communication within the Ghost framework
    /// </summary>
    public class SystemEvent
    {
        /// <summary>
        /// Type of the event
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// ID of the process this event relates to
        /// </summary>
        public string ProcessId { get; set; }

        /// <summary>
        /// JSON-serialized data payload
        /// </summary>
        public string Data { get; set; }

        /// <summary>
        /// UTC timestamp when the event was created
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Create a new empty system event
        /// </summary>
        public SystemEvent() { }

        /// <summary>
        /// Create a new system event with the specified type and process ID
        /// </summary>
        /// <param name="type">Event type</param>
        /// <param name="processId">Related process ID</param>
        public SystemEvent(string type, string processId)
        {
            Type = type;
            ProcessId = processId;
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Create a new system event with the specified type, process ID, and data
        /// </summary>
        /// <param name="type">Event type</param>
        /// <param name="processId">Related process ID</param>
        /// <param name="data">Event data (will be serialized to JSON)</param>
        public SystemEvent(string type, string processId, object data)
        {
            Type = type;
            ProcessId = processId;
            Data = data != null ? JsonSerializer.Serialize(data) : null;
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Deserialize the Data property to a specific type
        /// </summary>
        /// <typeparam name="T">Type to deserialize to</typeparam>
        /// <returns>Deserialized object or null if Data is empty</returns>
        public T GetData<T>() where T : class
        {
            if (string.IsNullOrEmpty(Data)) return null;
            return JsonSerializer.Deserialize<T>(Data);
        }

        /// <summary>
        /// Create a process registration event
        /// </summary>
        /// <param name="processId">Process ID</param>
        /// <param name="registration">Registration data</param>
        /// <returns>New SystemEvent</returns>
        public static SystemEvent CreateRegistration(string processId, ProcessRegistration registration)
        {
            return new SystemEvent
            {
                Type = EventTypes.ProcessRegistered,
                ProcessId = processId,
                Data = JsonSerializer.Serialize(registration),
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Create a process started event
        /// </summary>
        /// <param name="processId">Process ID</param>
        /// <returns>New SystemEvent</returns>
        public static SystemEvent CreateStarted(string processId)
        {
            return new SystemEvent
            {
                Type = EventTypes.ProcessStarted,
                ProcessId = processId,
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Create a process stopped event
        /// </summary>
        /// <param name="processId">Process ID</param>
        /// <returns>New SystemEvent</returns>
        public static SystemEvent CreateStopped(string processId)
        {
            return new SystemEvent
            {
                Type = EventTypes.ProcessStopped,
                ProcessId = processId,
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Create a process crashed event
        /// </summary>
        /// <param name="processId">Process ID</param>
        /// <param name="errorInfo">Error information</param>
        /// <returns>New SystemEvent</returns>
        public static SystemEvent CreateCrashed(string processId, object errorInfo = null)
        {
            return new SystemEvent
            {
                Type = EventTypes.ProcessCrashed,
                ProcessId = processId,
                Data = errorInfo != null ? JsonSerializer.Serialize(errorInfo) : null,
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Get a log-friendly representation of the event
        /// </summary>
        public override string ToString()
        {
            return $"{Type} ({ProcessId}) @ {Timestamp:yyyy-MM-dd HH:mm:ss.fff}";
        }

        /// <summary>
        /// Common event type constants
        /// </summary>
        public static class EventTypes
        {
            /// <summary>Process is registering with GhostFather</summary>
            public const string ProcessRegistered = "process.registered";
            
            /// <summary>Process has started</summary>
            public const string ProcessStarted = "process.started";
            
            /// <summary>Process has stopped</summary>
            public const string ProcessStopped = "process.stopped";
            
            /// <summary>Process has crashed</summary>
            public const string ProcessCrashed = "process.crashed";
            
            /// <summary>Process has been restarted</summary>
            public const string ProcessRestarted = "process.restarted";
            
            /// <summary>Process resources exceeded limits</summary>
            public const string ProcessResourcesExceeded = "process.resources.exceeded";
            
            /// <summary>Health status changed</summary>
            public const string HealthStatusChanged = "health.status.changed";
            
            /// <summary>Ghost daemon started</summary>
            public const string DaemonStarted = "daemon.started";
            
            /// <summary>Ghost daemon stopping</summary>
            public const string DaemonStopping = "daemon.stopping";
            
            /// <summary>Configuration changed</summary>
            public const string ConfigChanged = "config.changed";
        }
    }
}