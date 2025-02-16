namespace Ghost.Core.Data;

public class StorageException : Exception
{

  public StorageException(string message, StorageErrorCode code = StorageErrorCode.Unknown)
      : base(message)
  {
    ErrorCode = code;
  }

  public StorageException(string message, Exception innerException, StorageErrorCode code = StorageErrorCode.Unknown)
      : base(message, innerException)
  {
    ErrorCode = code;
  }
  public StorageErrorCode ErrorCode { get; }
}
