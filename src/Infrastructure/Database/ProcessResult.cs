namespace Ghost.Infrastructure.Database;

public class ProcessResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; }
    public string StandardError { get; init; }
    public bool Success => ExitCode == 0;

    public void EnsureSuccessfulExit()
    {
        if (!Success)
        {
            var error = string.IsNullOrEmpty(StandardError)
                    ? StandardOutput
                    : StandardError;

            throw new GhostException(
                    $"Process failed with exit code {ExitCode}: {error}",
                    ErrorCode.ProcessError);
        }
    }
}