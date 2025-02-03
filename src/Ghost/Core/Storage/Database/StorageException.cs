namespace Ghost.Core.Storage;

public class StorageException : Exception
{
  public StorageErrorCode ErrorCode { get; }

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
}
