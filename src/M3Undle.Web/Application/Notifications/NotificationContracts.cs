namespace M3Undle.Web.Application.Notifications;

public enum NotificationSendOutcome
{
    /// <summary>The transport accepted the message. This is not proof that a person received or read it.</summary>
    Accepted,
    RetryableFailure,
    PermanentFailure,

    /// <summary>The message may already have been accepted, so an automatic retry could duplicate it.</summary>
    Uncertain,
}

public sealed record NotificationSendResult(
    NotificationSendOutcome Outcome,
    string? ErrorCode = null,
    string? ErrorText = null,
    TimeSpan? RetryAfter = null,
    string? RemoteReference = null)
{
    public static NotificationSendResult Accepted(string? remoteReference = null) =>
        new(NotificationSendOutcome.Accepted, RemoteReference: remoteReference);

    public static NotificationSendResult Retryable(string code, string text, TimeSpan? retryAfter = null) =>
        new(NotificationSendOutcome.RetryableFailure, code, text, retryAfter);

    public static NotificationSendResult Permanent(string code, string text) =>
        new(NotificationSendOutcome.PermanentFailure, code, text);

    public static NotificationSendResult Uncertain(string code, string text) =>
        new(NotificationSendOutcome.Uncertain, code, text);
}

/// <summary>A decrypted secret held only in short-lived server memory. It never serializes or prints.</summary>
public sealed class NotificationSecret(string value)
{
    public string Reveal() => value;
    public override string ToString() => "[redacted]";
}

/// <summary>Provider-neutral logical content. Each provider does its own final protocol formatting.</summary>
public sealed record NotificationMessage(
    string Title,
    string Body,
    string Severity,
    string? LinkUrl,
    DateTime OccurredUtc);

/// <summary>One resolved destination, e.g. a single mailbox or the Matrix room.</summary>
public sealed record NotificationTarget(string TargetId, string Label);

/// <summary>Typed, provider-specific resolved configuration. Providers verify the kind before using it.</summary>
public abstract record NotificationProviderConfiguration(string Kind);

public sealed record MatrixProviderConfiguration(
    string HomeserverUrl,
    string RoomId,
    NotificationSecret AccessToken,
    string? BotUserId,
    string? DeviceId,
    bool AllowInsecureHttp) : NotificationProviderConfiguration(Data.Entities.NotificationProviderKinds.Matrix);

public sealed record SmtpProviderConfiguration(
    string Host,
    int Port,
    string TlsMode,
    string AuthMode,
    string? Username,
    NotificationSecret? Password,
    string SenderAddress,
    string? SenderName) : NotificationProviderConfiguration(Data.Entities.NotificationProviderKinds.Smtp);

public sealed record NotificationSendRequest(
    string DeliveryId,
    string MessageId,
    NotificationMessage Message,
    NotificationTarget Target,
    int ConfigRevision,
    NotificationProviderConfiguration Configuration);

/// <summary>
/// A transport. Providers classify outcomes; they do not retry, persist, or decide routing.
/// Host cancellation must propagate as <see cref="OperationCanceledException"/>.
/// </summary>
public interface INotificationProvider
{
    string Kind { get; }

    /// <summary>
    /// True when re-sending the same delivery is safe because the transport deduplicates it (for example a Matrix
    /// transaction ID under a preserved room and device). The worker uses this to decide between an automatic retry and
    /// an Uncertain outcome after an interrupted send; it never switches on <see cref="Kind"/>.
    /// </summary>
    bool SupportsIdempotentRetry => false;

    Task<NotificationSendResult> SendAsync(NotificationSendRequest request, CancellationToken cancellationToken);
}

public sealed record NotificationConnectionCheck(
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorText = null,
    IReadOnlyDictionary<string, string>? Discovered = null);

/// <summary>
/// Optional capability: validate a destination before test messages are sent (identity, membership, permissions,
/// supported room state). Whatever the check discovers is persisted through the destination adapter.
/// </summary>
public interface INotificationConnectionValidator
{
    Task<NotificationConnectionCheck> CheckAsync(NotificationProviderConfiguration configuration, CancellationToken cancellationToken);
}

/// <summary>Resolves registered providers by stable kind. Adding a provider needs registration only.</summary>
public sealed class NotificationProviderRegistry
{
    private readonly Dictionary<string, INotificationProvider> _providers;

    public NotificationProviderRegistry(IEnumerable<INotificationProvider> providers)
    {
        _providers = new Dictionary<string, INotificationProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (!_providers.TryAdd(provider.Kind, provider))
                throw new InvalidOperationException($"Notification provider kind '{provider.Kind}' is registered more than once.");
        }
    }

    public IReadOnlyCollection<string> Kinds => _providers.Keys;

    public INotificationProvider? Find(string kind) => _providers.GetValueOrDefault(kind);
}
