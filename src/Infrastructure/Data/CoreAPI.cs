using Ghost.Infrastructure;
using Ghost.Infrastructure.Data;
using Ghost.Infrastructure.Monitoring;
using Ghost.Infrastructure.Orchestration;
using Ghost.Infrastructure.Orchestration.Channels;

namespace Ghost.SDK;

/// <summary>
/// Implementation of the Core API interface
/// This is like the "receptionist" who knows how to route requests to the right department
/// </summary>
public class CoreAPI : ICoreAPI
{
  private readonly IDataAPI _dataApi;
  private readonly IRedisManager _redisManager;
  private readonly IConfigManager _configManager;

  public CoreAPI(
      IDataAPI dataApi,
      IRedisManager redisManager,
      IConfigManager configManager)
  {
    _dataApi = dataApi;
    _redisManager = redisManager;
    _configManager = configManager;
  }

  public async Task<T> InvokeOperationAsync<T>(string operationType, object parameters)
  {
    // Validate operation type
    if (string.IsNullOrEmpty(operationType))
      throw new ArgumentNullException(nameof(operationType));

    try
    {
      // Convert parameters to expected format
      var systemCommand = new SystemCommand(
          operationType,
          TargetProcessId: Guid.NewGuid().ToString(),
          Parameters: ConvertToParameterDictionary(parameters)
      );

      // Publish command and await result
      await _redisManager.PublishSystemCommandAsync(systemCommand);

      // Wait for and return result
      // In a real implementation, this would involve setting up a response channel
      // and waiting for the specific result with a timeout
      throw new NotImplementedException("Response handling to be implemented");
    }
    catch (Exception ex)
    {
      throw new GhostException(
          $"Failed to invoke operation {operationType}",
          ex,
          ErrorCode.ProcessError
      );
    }
  }

  public async Task PublishEventAsync(string eventType, object payload)
  {
    if (string.IsNullOrEmpty(eventType))
      throw new ArgumentNullException(nameof(eventType));

    try
    {
      await _redisManager.PublishSystemCommandAsync(new SystemCommand(
          "publish_event",
          TargetProcessId: Guid.NewGuid().ToString(),
          Parameters: new Dictionary<string, string>
          {
              ["eventType"] = eventType,
              ["payload"] = System.Text.Json.JsonSerializer.Serialize(payload)
          }
      ));
    }
    catch (Exception ex)
    {
      throw new GhostException(
          $"Failed to publish event {eventType}",
          ex,
          ErrorCode.ProcessError
      );
    }
  }

  public async Task<bool> IsHealthyAsync()
  {
    try
    {
      // Check core service health
      var metrics = await GetSystemMetricsAsync();
      return metrics.Status == "healthy";
    }
    catch
    {
      return false;
    }
  }

  public async Task<SystemMetrics> GetSystemMetricsAsync()
  {
    try
    {
      var metrics = await _dataApi.GetDataAsync<SystemMetrics>("system:metrics");
      return metrics ?? new SystemMetrics
      {
          Status = "unknown",
          LastUpdate = DateTime.UtcNow,
          Processes = new Dictionary<string, ProcessMetrics>()
      };
    }
    catch (Exception ex)
    {
      throw new GhostException(
          "Failed to get system metrics",
          ex,
          ErrorCode.ProcessError
      );
    }
  }

  private Dictionary<string, string> ConvertToParameterDictionary(object parameters)
  {
    if (parameters == null)
      return new Dictionary<string, string>();

    return parameters.GetType()
        .GetProperties()
        .ToDictionary(
            p => p.Name,
            p => Convert.ToString(p.GetValue(parameters)) ?? string.Empty
        );
  }
}

