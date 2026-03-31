namespace M3Undle.Web.Streaming.Sessions;

public sealed class StreamAdmissionException(
    string message,
    StreamAdmissionFailureKind failureKind,
    int statusCode = StatusCodes.Status503ServiceUnavailable,
    int? retryAfterSeconds = null)
    : Exception(message)
{
    public StreamAdmissionFailureKind FailureKind { get; } = failureKind;

    public int StatusCode { get; } = statusCode;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

