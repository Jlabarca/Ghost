namespace Ghost.Core.Config.Ghost.Core.Config.Ghost.Core.Config;

public class StateEntry
{
  public string Key { get; set; }
  public object Value { get; set; }
  public StateChangeType ChangeType { get; set; }
  public DateTime Timestamp { get; set; }
}
