namespace M3Undle.Web.Streaming.Sessions;

public sealed class InternalRelaySecretService
{
    public string Secret { get; } = Guid.NewGuid().ToString("N");
}
