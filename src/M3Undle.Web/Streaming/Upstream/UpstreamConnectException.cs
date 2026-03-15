using M3Undle.Web.Streaming.Models;

namespace M3Undle.Web.Streaming.Upstream;

public sealed class UpstreamConnectException(
    string message,
    UpstreamFailureKind failureKind,
    int? statusCode = null,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public UpstreamFailureKind FailureKind { get; } = failureKind;

    public int? StatusCode { get; } = statusCode;
}

