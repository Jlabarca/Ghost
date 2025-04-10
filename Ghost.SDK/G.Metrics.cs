using Ghost.SDK;

namespace Ghost;

public partial class Ghost
{
  public static Task TrackMetricAsync(string name, double value, Dictionary<string, string> tags = null)
    => GhostProcess.Instance.TrackMetricAsync(name, value, tags);

  public static Task TrackEventAsync(string name, Dictionary<string, string> properties = null)
    => GhostProcess.Instance.TrackEventAsync(name, properties);

  // Data
  public static Task<int> ExecuteAsync(string sql, object param = null)
    => GhostProcess.Instance.ExecuteAsync(sql, param);

  public static Task<IEnumerable<T>> QueryAsync<T>(string sql, object param = null)
    => GhostProcess.Instance.QueryAsync<T>(sql, param);

  public static string GetSetting(string name, string defaultValue = null)
    => GhostProcess.Instance.GetSetting(name, defaultValue);
}
