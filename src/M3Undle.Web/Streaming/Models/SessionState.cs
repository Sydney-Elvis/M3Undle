namespace M3Undle.Web.Streaming.Models;

public enum SessionState
{
    Initializing = 0,
    Connecting = 1,
    Live = 2,
    Reconnecting = 3,
    Closed = 5,
    Faulted = 6,
}

