namespace M3Undle.Web.Logging;

public sealed class InMemoryLogStore
{
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly LinkedList<LogEntry> _entries = new();

    public InMemoryLogStore(int capacity = 200)
    {
        _capacity = capacity;
    }

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _entries.AddLast(entry);
            if (_entries.Count > _capacity)
                _entries.RemoveFirst();
        }
    }

    public IReadOnlyList<LogEntry> GetRecent()
    {
        lock (_lock)
        {
            return [.. _entries];
        }
    }
}

