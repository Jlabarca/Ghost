namespace Ghost.Legacy.Infrastructure;

public enum ErrorCode
{
  RepositoryNotFound,
  BuildFailed,
  AliasError,
  AliasConflict,
  ProcessError,
  DirectoryNotFound,
  TokenNotFound,
  GithubError,
  GitConfigMissing,
  ValidationError
}

public class GhostException : Exception
{
  public ErrorCode Code { get; }
  public string UserMessage { get; }

  public GhostException(string userMessage, ErrorCode code = ErrorCode.ProcessError)
      : base(userMessage)
  {
    Code = code;
    UserMessage = userMessage;
  }
}

