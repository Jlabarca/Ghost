namespace Ghost.Core;

public enum ErrorCode
{
    // Storage related
    StorageConnectionFailed,
    StorageOperationFailed,
    CacheMiss,
    
    // Permission related
    UnauthorizedAccess,
    InsufficientPermissions,
    
    // Process related
    ProcessStartFailed,
    ProcessTerminated,
    
    // Configuration related
    ConfigurationError,
    ValidationError,
    
    // General
    Unknown,
    NotImplemented,
    InvalidOperation,
    ProcessError,
    TemplateError,
    GitError,
    TemplateNotFound,
    StorageConfigurationFailed
}

public class GhostException : Exception
{
    public ErrorCode Code { get; }
    public string Details { get; }
    public Dictionary<string, string> Context { get; }

    public GhostException(string message, ErrorCode code = ErrorCode.Unknown, string details = null, Dictionary<string, string> context = null) 
        : base(message)
    {
        Code = code;
        Details = details;
        Context = context ?? new Dictionary<string, string>();
    }

    public GhostException(string message, Exception innerException, ErrorCode code = ErrorCode.Unknown) 
        : base(message, innerException)
    {
        Code = code;
    }
}
