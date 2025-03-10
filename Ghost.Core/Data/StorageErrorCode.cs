namespace Ghost.Core.Data;

public enum StorageErrorCode
{
  Unknown,
  ConnectionFailed,
  OperationFailed,
  NotFound,
  AlreadyExists,
  InvalidOperation,
  Timeout,
  SchemaError,
  DataError,
  ValidationError
}
