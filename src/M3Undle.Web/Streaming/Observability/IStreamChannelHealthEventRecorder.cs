namespace M3Undle.Web.Streaming.Observability;

public interface IStreamChannelHealthEventRecorder
{
    void Record(StreamDiagnosticEvent diagnosticEvent);
    Task FlushAsync(CancellationToken ct = default);
}

public sealed class NoopStreamChannelHealthEventRecorder : IStreamChannelHealthEventRecorder
{
    public static NoopStreamChannelHealthEventRecorder Instance { get; } = new();

    private NoopStreamChannelHealthEventRecorder()
    {
    }

    public void Record(StreamDiagnosticEvent diagnosticEvent)
    {
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}
