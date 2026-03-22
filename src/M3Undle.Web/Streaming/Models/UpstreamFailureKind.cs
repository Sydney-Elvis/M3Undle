namespace M3Undle.Web.Streaming.Models;

public enum UpstreamFailureKind
{
    Unknown = 0,
    StartupFatal = 1,
    UpstreamAuth = 2,
    UpstreamNotFound = 3,
    UpstreamServerError = 4,
    Transport = 5,
    TimeoutOrStall = 6,
    EndOfStream = 7,
}

