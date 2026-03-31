namespace M3Undle.Web.Streaming.Sessions;

public enum StreamAdmissionFailureKind
{
    Cooldown = 0,
    MaxConcurrentSessions = 1,
    ProviderLimit = 2,
}
