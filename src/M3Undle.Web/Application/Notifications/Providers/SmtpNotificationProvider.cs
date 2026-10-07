using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Notifications.Providers;

/// <summary>
/// Administrator email over SMTP. It always requires TLS (STARTTLS or TLS on connect) with normal certificate validation
/// and never falls back to plaintext, so credentials are only ever sent over an encrypted channel. The one delivery maps to
/// one envelope recipient, so a partial failure never forces other recipients to be re-sent.
///
/// The outcome boundary is the DATA phase: a failure before the message body went out is a clean retry, a definite SMTP
/// reply is classified by its status, and a failure after the body was sent but before the final reply is Uncertain because
/// the server may already have accepted it. A failure while closing the connection after acceptance never undoes it.
/// </summary>
public sealed class SmtpNotificationProvider(
    ILogger<SmtpNotificationProvider> logger,
    Func<SmtpClient>? clientFactory = null) : INotificationProvider
{
    public const int ConnectTimeoutMilliseconds = 15_000;
    private const int MaxSubjectLength = 200;
    private const int MaxBodyLength = 8_000;

    public string Kind => NotificationProviderKinds.Smtp;

    public async Task<NotificationSendResult> SendAsync(NotificationSendRequest request, CancellationToken cancellationToken)
    {
        if (request.Configuration is not SmtpProviderConfiguration config)
            return NotificationSendResult.Permanent("configuration", "The SMTP configuration is not valid.");

        MimeMessage message;
        MailboxAddress recipient;
        try
        {
            (message, recipient) = BuildMessage(request, config);
        }
        catch (Exception ex) when (ex is ParseException or FormatException or ArgumentException)
        {
            return NotificationSendResult.Permanent("invalid_message", "The message or recipient address was not valid.");
        }

        using var client = clientFactory?.Invoke() ?? new SmtpClient();
        client.Timeout = ConnectTimeoutMilliseconds;
        var progress = new DataPhaseProgress();
        string? serverReply = null;
        client.MessageSent += (_, e) => serverReply = e.Response;

        try
        {
            await client.ConnectAsync(
                config.Host, config.Port,
                config.TlsMode == "tls" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken);

            if (config.AuthMode == "password")
            {
                if (string.IsNullOrEmpty(config.Username) || config.Password is null)
                    return NotificationSendResult.Permanent("configuration", "SMTP credentials are not configured.");
                await client.AuthenticateAsync(config.Username, config.Password.Reveal(), cancellationToken);
            }

            var sender = new MailboxAddress(string.Empty, config.SenderAddress);
            await client.SendAsync(message, sender, [recipient], cancellationToken, progress);

            await DisconnectQuietlyAsync(client);
            return NotificationSendResult.Accepted(SafeReference(serverReply, message.MessageId));
        }
        catch (OperationCanceledException)
        {
            // Pre-DATA the message cannot have been accepted, so it is a clean retry; afterwards acceptance is unknown.
            return progress.Started
                ? NotificationSendResult.Uncertain("cancelled", "The send was interrupted after the message body was transmitted.")
                : NotificationSendResult.Retryable("cancelled", "The send was interrupted before the message was transmitted.");
        }
        catch (Exception ex)
        {
            return Classify(ex, progress.Started, config);
        }
    }

    private static (MimeMessage Message, MailboxAddress Recipient) BuildMessage(NotificationSendRequest request, SmtpProviderConfiguration config)
    {
        var recipient = MailboxAddress.Parse(request.Target.Label);
        var from = new MailboxAddress(StripControl(config.SenderName ?? "M3Undle"), config.SenderAddress);

        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(recipient);
        message.Subject = Truncate(StripControl(request.Message.Title), MaxSubjectLength);
        message.MessageId = request.MessageId.Trim('<', '>');
        message.Date = request.Message.OccurredUtc;
        message.Headers.Add("Auto-Submitted", "auto-generated");
        message.Headers.Add("X-M3Undle-Severity", StripControl(request.Message.Severity));

        var body = request.Message.Body;
        if (!string.IsNullOrWhiteSpace(request.Message.LinkUrl))
            body += $"\n\nOpen M3Undle: {request.Message.LinkUrl}";
        body += "\n\n-- \nSent by M3Undle. An accepted message is not proof that anyone read it.";
        message.Body = new TextPart("plain") { Text = Truncate(body, MaxBodyLength) };
        return (message, recipient);
    }

    private NotificationSendResult Classify(Exception ex, bool dataStarted, SmtpProviderConfiguration config)
    {
        switch (ex)
        {
            case AuthenticationException:
                return NotificationSendResult.Permanent("auth_failed", "The SMTP server rejected the username or password.");

            case SslHandshakeException handshake:
                // The reason (untrusted CA, name mismatch, expiry, revocation) is operator-actionable and holds no secret.
                logger.LogWarning("SMTP TLS validation failed for {Host}:{Port}: {Reason}", config.Host, config.Port, handshake.InnerException?.Message ?? handshake.Message);
                return NotificationSendResult.Permanent("tls_failed", "The SMTP server's TLS certificate could not be validated (untrusted authority, wrong name, expired, revoked, or no revocation information published).");

            case NotSupportedException:
                return NotificationSendResult.Permanent("tls_unsupported", "The SMTP server does not offer the required TLS.");

            case SmtpCommandException command:
                var code = (int)command.StatusCode;
                if (code is 530 or 534 or 535 or 538)
                    return NotificationSendResult.Permanent("auth_failed", $"The SMTP server refused authentication (SMTP {code}).");
                return code is >= 400 and < 500
                    ? NotificationSendResult.Retryable("smtp_temporary", $"The SMTP server deferred the message (SMTP {code}).")
                    : NotificationSendResult.Permanent("smtp_rejected", $"The SMTP server rejected the message (SMTP {code}).");

            case SmtpProtocolException or IOException or System.Net.Sockets.SocketException or TimeoutException or ServiceNotConnectedException:
                logger.LogDebug(ex, "SMTP transport failure (data phase started: {DataStarted}).", dataStarted);
                return dataStarted
                    ? NotificationSendResult.Uncertain("ack_lost", "The connection failed after the message was sent, so it may have been accepted.")
                    : NotificationSendResult.Retryable("network", "The SMTP server could not be reached or the connection dropped.");

            default:
                logger.LogWarning(ex, "Unexpected SMTP failure.");
                return dataStarted
                    ? NotificationSendResult.Uncertain("unexpected", "The send failed unexpectedly after the message was sent.")
                    : NotificationSendResult.Retryable("unexpected", "The send failed unexpectedly before the message was sent.");
        }
    }

    // Closing the connection is cleanup, not part of acceptance: a failure here must never turn an accepted message into a retry.
    private async Task DisconnectQuietlyAsync(SmtpClient client)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(true, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SMTP disconnect failed after the message was accepted; ignoring.");
        }
    }

    private static string? SafeReference(string? reply, string? messageId)
    {
        var text = string.IsNullOrWhiteSpace(reply) ? messageId : reply;
        return text is null ? null : Truncate(StripControl(text), 120);
    }

    private static string StripControl(string value) =>
        new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed class DataPhaseProgress : ITransferProgress
    {
        private int _started;
        public bool Started => Volatile.Read(ref _started) == 1;
        public void Report(long bytesTransferred, long totalSize) => Interlocked.Exchange(ref _started, 1);
        public void Report(long bytesTransferred) => Interlocked.Exchange(ref _started, 1);
    }
}
