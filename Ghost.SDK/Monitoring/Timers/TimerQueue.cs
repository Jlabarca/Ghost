// using System.Diagnostics;
//
// namespace ProcessManager
// {
//     /// <summary>
//     /// Represents a scheduling strategy for a process task
//     /// </summary>
//     public interface IScheduleStrategy
//     {
//         /// <summary>
//         /// Calculates the next execution time
//         /// </summary>
//         /// <param name="lastExecutionTime">The time of the last execution, or null if never executed</param>
//         /// <returns>The next time this task should execute, or null if it should not execute again</returns>
//         DateTime? GetNextExecutionTime(DateTime? lastExecutionTime);
//     }
//
//     /// <summary>
//     /// A simple interval-based scheduling strategy
//     /// </summary>
//     public class IntervalScheduleStrategy : IScheduleStrategy
//     {
//         private readonly TimeSpan _interval;
//         private readonly int _maxExecutions;
//         private int _executionCount;
//
//         public IntervalScheduleStrategy(TimeSpan interval, int maxExecutions = -1)
//         {
//             _interval = interval;
//             _maxExecutions = maxExecutions;
//         }
//
//         public DateTime? GetNextExecutionTime(DateTime? lastExecutionTime)
//         {
//             if (_maxExecutions > 0 && _executionCount >= _maxExecutions)
//                 return null;
//
//             _executionCount++;
//             return (lastExecutionTime ?? DateTime.UtcNow) + _interval;
//         }
//     }
//
//     /// <summary>
//     /// A cron-like scheduling strategy
//     /// </summary>
//     public class CronScheduleStrategy : IScheduleStrategy
//     {
//         // A simplified cron expression parser could be implemented here
//         // For now, we'll use a basic approach with predefined schedules
//         private readonly Func<DateTime, DateTime?> _getNextExecution;
//
//         // Example: daily at specific time
//         public static CronScheduleStrategy Daily(int hour, int minute) =>
//             new CronScheduleStrategy(dt =>
//             {
//                 var next = new DateTime(dt.Year, dt.Month, dt.Day, hour, minute, 0, DateTimeKind.Utc);
//                 if (next <= dt)
//                     next = next.AddDays(1);
//                 return next;
//             });
//
//         // Example: weekly on specific day and time
//         public static CronScheduleStrategy Weekly(DayOfWeek day, int hour, int minute) =>
//             new CronScheduleStrategy(dt =>
//             {
//                 var next = new DateTime(dt.Year, dt.Month, dt.Day, hour, minute, 0, DateTimeKind.Utc);
//                 int daysToAdd = ((int)day - (int)dt.DayOfWeek + 7) % 7;
//                 if (daysToAdd == 0 && next <= dt)
//                     daysToAdd = 7;
//                 return next.AddDays(daysToAdd);
//             });
//
//         private CronScheduleStrategy(Func<DateTime, DateTime?> getNextExecution)
//         {
//             _getNextExecution = getNextExecution;
//         }
//
//         public DateTime? GetNextExecutionTime(DateTime? lastExecutionTime)
//         {
//             return _getNextExecution(lastExecutionTime ?? DateTime.UtcNow);
//         }
//     }
//
//     /// <summary>
//     /// Represents a managed process
//     /// </summary>
//     public class ManagedProcess
//     {
//         public string Id { get; }
//         public string Command { get; }
//         public string Arguments { get; }
//         public IScheduleStrategy ScheduleStrategy { get; }
//         public Process? Process { get; private set; }
//         public DateTime? LastStartTime { get; private set; }
//         public DateTime? NextExecutionTime { get; private set; }
//         public ProcessStatus Status { get; private set; }
//         public event EventHandler<ProcessEventArgs>? OnProcessEvent;
//
//         public ManagedProcess(string id, string command, string arguments, IScheduleStrategy scheduleStrategy)
//         {
//             Id = id;
//             Command = command;
//             Arguments = arguments;
//             ScheduleStrategy = scheduleStrategy;
//             Status = ProcessStatus.Idle;
//             CalculateNextExecutionTime();
//         }
//
//         public void CalculateNextExecutionTime()
//         {
//             NextExecutionTime = ScheduleStrategy.GetNextExecutionTime(LastStartTime);
//         }
//
//         public void Start()
//         {
//             if (Status == ProcessStatus.Running)
//                 return;
//
//             try
//             {
//                 Process = new Process
//                 {
//                     StartInfo = new ProcessStartInfo
//                     {
//                         FileName = Command,
//                         Arguments = Arguments,
//                         UseShellExecute = false,
//                         RedirectStandardOutput = true,
//                         RedirectStandardError = true,
//                         CreateNoWindow = true
//                     },
//                     EnableRaisingEvents = true
//                 };
//
//                 Process.Exited += (sender, args) =>
//                 {
//                     Status = ProcessStatus.Exited;
//                     RaiseEvent(ProcessEventType.Exited);
//                     CalculateNextExecutionTime();
//                 };
//
//                 Process.Start();
//                 LastStartTime = DateTime.UtcNow;
//                 Status = ProcessStatus.Running;
//                 RaiseEvent(ProcessEventType.Started);
//             }
//             catch (Exception ex)
//             {
//                 Status = ProcessStatus.Failed;
//                 RaiseEvent(ProcessEventType.Failed, ex.Message);
//                 CalculateNextExecutionTime();
//             }
//         }
//
//         public void Stop()
//         {
//             if (Status != ProcessStatus.Running || Process == null)
//                 return;
//
//             try
//             {
//                 if (!Process.HasExited)
//                 {
//                     Process.Kill();
//                     Status = ProcessStatus.Stopping;
//                     RaiseEvent(ProcessEventType.Stopping);
//                 }
//             }
//             catch (Exception ex)
//             {
//                 RaiseEvent(ProcessEventType.Failed, ex.Message);
//             }
//         }
//
//         private void RaiseEvent(ProcessEventType eventType, string? message = null)
//         {
//             OnProcessEvent?.Invoke(this, new ProcessEventArgs(Id, eventType, message));
//         }
//     }
//
//     public enum ProcessStatus
//     {
//         Idle,
//         Running,
//         Stopping,
//         Exited,
//         Failed
//     }
//
//     public enum ProcessEventType
//     {
//         Started,
//         Stopping,
//         Exited,
//         Failed
//     }
//
//     public class ProcessEventArgs : EventArgs
//     {
//         public string ProcessId { get; }
//         public ProcessEventType EventType { get; }
//         public string? Message { get; }
//
//         public ProcessEventArgs(string processId, ProcessEventType eventType, string? message = null)
//         {
//             ProcessId = processId;
//             EventType = eventType;
//             Message = message;
//         }
//     }
//
//     /// <summary>
//     /// Process manager that handles scheduling and running processes
//     /// </summary>
//     public class ProcessManager : IDisposable
//     {
//         private readonly Dictionary<string, ManagedProcess> _processes = new();
//         private readonly SortedList<DateTime, List<string>> _scheduledExecutions = new();
//         private readonly Timer _timer;
//         private readonly object _lock = new();
//         private readonly TimeSpan _minTimerInterval = TimeSpan.FromMilliseconds(100); // Minimum timer interval
//         private DateTime _nextTimerDue = DateTime.MaxValue;
//         private bool _disposed;
//
//         public event EventHandler<ProcessEventArgs>? OnProcessEvent;
//
//         public ProcessManager()
//         {
//             // Timer that will check for processes to start
//             _timer = new Timer(CheckScheduledProcesses, null, Timeout.Infinite, Timeout.Infinite);
//         }
//
//         public void RegisterProcess(string id, string command, string arguments, IScheduleStrategy scheduleStrategy)
//         {
//             lock (_lock)
//             {
//                 if (_processes.ContainsKey(id))
//                     throw new ArgumentException($"Process with ID '{id}' already registered", nameof(id));
//
//                 var process = new ManagedProcess(id, command, arguments, scheduleStrategy);
//                 process.OnProcessEvent += (sender, args) => OnProcessEvent?.Invoke(sender, args);
//                 _processes.Add(id, process);
//
//                 if (process.NextExecutionTime.HasValue)
//                 {
//                     ScheduleProcessExecution(id, process.NextExecutionTime.Value);
//                 }
//
//                 UpdateTimer();
//             }
//         }
//
//         public void UnregisterProcess(string id)
//         {
//             lock (_lock)
//             {
//                 if (_processes.TryGetValue(id, out var process))
//                 {
//                     process.Stop();
//                     _processes.Remove(id);
//
//                     // Remove from scheduled executions
//                     foreach (var kvp in _scheduledExecutions.ToList())
//                     {
//                         kvp.Value.Remove(id);
//                         if (kvp.Value.Count == 0)
//                             _scheduledExecutions.Remove(kvp.Key);
//                     }
//
//                     UpdateTimer();
//                 }
//             }
//         }
//
//         public void StartProcess(string id)
//         {
//             lock (_lock)
//             {
//                 if (_processes.TryGetValue(id, out var process))
//                 {
//                     process.Start();
//                 }
//             }
//         }
//
//         public void StopProcess(string id)
//         {
//             lock (_lock)
//             {
//                 if (_processes.TryGetValue(id, out var process))
//                 {
//                     process.Stop();
//                 }
//             }
//         }
//
//         public IReadOnlyCollection<ManagedProcess> GetProcesses()
//         {
//             lock (_lock)
//             {
//                 return _processes.Values.ToList().AsReadOnly();
//             }
//         }
//
//         private void ScheduleProcessExecution(string id, DateTime executionTime)
//         {
//             if (!_scheduledExecutions.TryGetValue(executionTime, out var processIds))
//             {
//                 processIds = new List<string>();
//                 _scheduledExecutions.Add(executionTime, processIds);
//             }
//
//             if (!processIds.Contains(id))
//             {
//                 processIds.Add(id);
//             }
//         }
//
//         private void CheckScheduledProcesses(object? state)
//         {
//             lock (_lock)
//             {
//                 if (_disposed) return;
//
//                 var now = DateTime.UtcNow;
//
//                 // Find all processes that need to be started
//                 var processesToStart = new List<string>();
//
//                 while (_scheduledExecutions.Count > 0 && _scheduledExecutions.Keys[0] <= now)
//                 {
//                     var executionTime = _scheduledExecutions.Keys[0];
//                     processesToStart.AddRange(_scheduledExecutions[executionTime]);
//                     _scheduledExecutions.RemoveAt(0);
//                 }
//
//                 // Start processes outside the loop to avoid modifying _scheduledExecutions during enumeration
//                 foreach (var processId in processesToStart)
//                 {
//                     if (_processes.TryGetValue(processId, out var process))
//                     {
//                         process.Start();
//
//                         // Schedule next execution if applicable
//                         process.CalculateNextExecutionTime();
//                         if (process.NextExecutionTime.HasValue)
//                         {
//                             ScheduleProcessExecution(processId, process.NextExecutionTime.Value);
//                         }
//                     }
//                 }
//
//                 // Update timer for next execution
//                 UpdateTimer();
//             }
//         }
//
//         private void UpdateTimer()
//         {
//             if (_disposed) return;
//
//             // Determine when to next wake up
//             if (_scheduledExecutions.Count > 0)
//             {
//                 var nextExecution = _scheduledExecutions.Keys[0];
//                 if (nextExecution != _nextTimerDue)
//                 {
//                     _nextTimerDue = nextExecution;
//                     var delay = _nextTimerDue - DateTime.UtcNow;
//
//                     // Ensure we don't set a negative delay or one that's too short
//                     var milliseconds = Math.Max(_minTimerIntervaG.TotalMilliseconds, delay.TotalMilliseconds);
//
//                     // Cap at int.MaxValue as that's what Timer accepts
//                     var timerDueTime = (int)Math.Min(milliseconds, int.MaxValue);
//                     _timer.Change(timerDueTime, Timeout.Infinite);
//                 }
//             }
//             else
//             {
//                 // No processes scheduled, disable timer
//                 _nextTimerDue = DateTime.MaxValue;
//                 _timer.Change(Timeout.Infinite, Timeout.Infinite);
//             }
//         }
//
//         public void Dispose()
//         {
//             lock (_lock)
//             {
//                 if (_disposed) return;
//                 _disposed = true;
//
//                 _timer.Dispose();
//
//                 // Stop all processes
//                 foreach (var process in _processes.Values)
//                 {
//                     process.Stop();
//                 }
//
//                 _processes.Clear();
//                 _scheduledExecutions.Clear();
//             }
//         }
//     }
// }