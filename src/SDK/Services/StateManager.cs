using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Orchestration;
using Ghost.Infrastructure.Orchestration.Channels;
namespace Ghost.SDK.Services;

public interface IStateManager
{
  /// <summary>
  /// Gets the current state of the process
  /// </summary>
  Task<ProcessState> GetCurrentStateAsync();

  /// <summary>
  /// Retrieves state history within a time range
  /// </summary>
  Task<IEnumerable<ProcessState>> GetStateHistoryAsync(DateTime from, DateTime to);

  /// <summary>
  /// Updates the current state with new status and properties
  /// </summary>
  Task UpdateStateAsync(string status, Dictionary<string, string> properties = null);

  /// <summary>
  /// Event raised when process state changes
  /// </summary>
  event EventHandler<StateChangedEventArgs> StateChanged;
}

public class StateChangedEventArgs : EventArgs
{
  public ProcessState OldState { get; }
  public ProcessState NewState { get; }

  public StateChangedEventArgs(ProcessState oldState, ProcessState newState)
  {
    OldState = oldState;
    NewState = newState;
  }
}

/// <summary>
/// Implements state management functionality.
/// Like a "state machine conductor" that orchestrates state transitions and maintains history.
/// </summary>
public class StateManager : IStateManager
{
  private readonly IRedisManager _redisManager;
  private readonly IDataAPI _dataApi;
  private readonly string _processId;
  private ProcessState _currentState;
  private readonly SemaphoreSlim _stateLock = new(1, 1);

  public event EventHandler<StateChangedEventArgs> StateChanged;

  public StateManager(IRedisManager redisManager, IDataAPI dataApi)
  {
    _redisManager = redisManager;
    _dataApi = dataApi;
    _processId = Guid.NewGuid().ToString();

    // Initialize with default state
    _currentState = new ProcessState(
        _processId,
        "initialized",
        new Dictionary<string, string>()
    );
  }

  public async Task<ProcessState> GetCurrentStateAsync()
  {
    await _stateLock.WaitAsync();
    try
    {
      return _currentState;
    }
    finally
    {
      _stateLock.Release();
    }
  }

  public async Task<IEnumerable<ProcessState>> GetStateHistoryAsync(DateTime from, DateTime to)
  {
    return await _dataApi.GetProcessHistoryAsync(_processId, from, to);
  }

  public async Task UpdateStateAsync(string status, Dictionary<string, string> properties = null)
  {
    if (string.IsNullOrEmpty(status))
      throw new ArgumentNullException(nameof(status));

    await _stateLock.WaitAsync();
    try
    {
      var oldState = _currentState;

      // Create new state with timestamp
      var newState = new ProcessState(
          _processId,
          status,
          new Dictionary<string, string>(properties ?? new Dictionary<string, string>())
          {
              ["timestamp"] = DateTime.UtcNow.ToString("o")
          }
      );

      // Publish state change
      await _redisManager.PublishStateAsync(_processId, newState);

      // Store in history
      await _dataApi.SetDataAsync(
          $"state:{_processId}:history:{DateTime.UtcNow:yyyyMMddHHmmss}",
          newState
      );

      _currentState = newState;

      OnStateChanged(oldState, newState);
    }
    finally
    {
      _stateLock.Release();
    }
  }

  protected virtual void OnStateChanged(ProcessState oldState, ProcessState newState)
  {
    StateChanged?.Invoke(this, new StateChangedEventArgs(oldState, newState));
  }
}
