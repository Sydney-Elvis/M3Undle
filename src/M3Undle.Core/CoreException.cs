namespace M3Undle.Core;

public sealed class CoreException : Exception
{
    public int ExitCode { get; }

    public CoreException(string message, int exitCode) : base(message)
    {
        ExitCode = exitCode;
    }
}
