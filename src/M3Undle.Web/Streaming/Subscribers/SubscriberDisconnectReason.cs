namespace M3Undle.Web.Streaming.Subscribers;

public enum SubscriberDisconnectReason
{
    Completed = 0,
    ClientAborted = 1,
    WriteFailure = 2,
    SlowClient = 3,
    SessionClosed = 4,
}

