using Serilog.Core;
using Serilog.Events;
using System.Threading.Channels;

namespace M3Undle.Web.Logging;

public sealed class LogBroadcastSink(InMemoryLogStore store) : ILogEventSink
{
    private readonly List<ChannelWriter<LogEntry>> _subscribers = [];
    private readonly Lock _lock = new();

    public void Emit(LogEvent logEvent)
    {
        var eventType = logEvent.Properties.TryGetValue("EventType", out var et)
            ? et.ToString().Trim('"')
            : string.Empty;

        var entry = new LogEntry(
            logEvent.Timestamp,
            logEvent.Level.ToString(),
            eventType,
            logEvent.RenderMessage(),
            logEvent.Exception?.ToString());

        store.Add(entry);

        lock (_lock)
        {
            _subscribers.RemoveAll(w => !w.TryWrite(entry));
        }
    }

    public ChannelReader<LogEntry> Subscribe(out IDisposable unsubscriber)
    {
        var channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(500)
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
        List<ChannelWriter<LogEntry>> subscribers,
        ChannelWriter<LogEntry> writer,
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

