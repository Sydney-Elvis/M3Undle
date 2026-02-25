namespace M3Undle.Web.Logging;

public record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string EventType,
    string Message,
    string? Exception);

