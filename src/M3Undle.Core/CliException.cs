namespace M3Undle.Core;

public sealed class CliException : Exception
{
    public int ExitCode { get; }

    public CliException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }
}

