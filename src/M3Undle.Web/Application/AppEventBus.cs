using System.Threading.Channels;

namespace M3Undle.Web.Application;

public enum AppEventKind { RefreshStarted, RefreshCompleted, ProviderChanged, ProviderActivated, GroupFiltersChanged }

public sealed record AppEvent(AppEventKind Kind, bool Succeeded = false, string? ErrorSummary = null);

public sealed class AppEventBus
{
    private readonly List<ChannelWriter<AppEvent>> _subscribers = [];
    private readonly Lock _lock = new();

    public void Publish(AppEventKind kind, bool succeeded = false, string? errorSummary = null)
    {
        var evt = new AppEvent(kind, succeeded, errorSummary);
        lock (_lock)
        {
            _subscribers.RemoveAll(w => !w.TryWrite(evt));
        }
    }

    public ChannelReader<AppEvent> Subscribe(out IDisposable unsubscriber)
    {
        var channel = Channel.CreateBounded<AppEvent>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        lock (_lock)
        {
            _subscribers.Add(channel.Writer);
        }

        unsubscriber = new ChannelUnsubscriber(_subscribers, channel.Writer, _lock);
        return channel.Reader;
    }

    private sealed class ChannelUnsubscriber(
        List<ChannelWriter<AppEvent>> subscribers,
        ChannelWriter<AppEvent> writer,
        Lock @lock) : IDisposable
    {
        public void Dispose()
        {
            lock (@lock)
            {
                subscribers.Remove(writer);
            }
            writer.TryComplete();
        }
    }
}

