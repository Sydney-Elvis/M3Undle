namespace M3Undle.Web.Streaming.Sessions;

public sealed class StreamAdmissionException(
    string message,
    int statusCode = StatusCodes.Status503ServiceUnavailable,
    int? retryAfterSeconds = null)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

