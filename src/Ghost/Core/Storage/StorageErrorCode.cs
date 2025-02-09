namespace Ghost.Core.Storage;

public enum StorageErrorCode
{
  Unknown,
  ConnectionFailed,
  OperationFailed,
  NotFound,
  AlreadyExists,
  InvalidOperation,
  Timeout
}
