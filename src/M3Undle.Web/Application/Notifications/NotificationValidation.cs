using System.Net.Mail;
using System.Text.RegularExpressions;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Notifications;

public sealed class NotificationValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool IsValid => _errors.Count == 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
            _errors[field] = list = [];
        list.Add(message);
    }

    public IDictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.Ordinal);
}

public static class NotificationLimits
{
    public const int MaxRecipients = 10;
    public const int MaxFieldLength = 512;
}

/// <summary>Structural validation. It never touches the network; it only says whether a saved setup could be tested.</summary>
public static partial class NotificationValidation
{
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();

    // Room IDs are opaque. Older room versions end in ":server"; newer ones (v12) are "!" plus an event hash with no server part.
    [GeneratedRegex(@"^![^\s:]+(:[^\s]+)?$")]
    private static partial Regex MatrixRoomIdPattern();

    public static bool HasControlCharacters(string? value) =>
        value is not null && value.Any(c => char.IsControl(c));

    /// <summary>A bare addr-spec only: no display name, comments, angle brackets, quotes or control characters.</summary>
    public static bool TryNormalizeMailbox(string? input, out string address, out string canonicalKey)
    {
        address = string.Empty;
        canonicalKey = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 254)
            return false;

        var trimmed = input.Trim();
        if (HasControlCharacters(trimmed) || trimmed.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '(' or ')' or ',' or ';'))
            return false;

        if (!MailAddress.TryCreate(trimmed, out var parsed) || !string.Equals(parsed.Address, trimmed, StringComparison.Ordinal))
            return false;

        var at = trimmed.LastIndexOf('@');
        if (at <= 0 || at == trimmed.Length - 1 || trimmed.IndexOf('@') != at)
            return false;

        address = trimmed;
        // Local parts can be case-sensitive; only the domain is case-insensitive.
        canonicalKey = string.Concat(trimmed.AsSpan(0, at), "@", trimmed[(at + 1)..].ToLowerInvariant());
        return true;
    }

    public static NotificationValidationErrors ValidateRecipients(IEnumerable<string?> addresses, out List<(string Address, string CanonicalKey)> normalized)
    {
        var errors = new NotificationValidationErrors();
        normalized = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = addresses.ToList();

        if (list.Count == 0)
            errors.Add("recipients", "Add at least one administrator mailbox.");
        if (list.Count > NotificationLimits.MaxRecipients)
            errors.Add("recipients", $"At most {NotificationLimits.MaxRecipients} recipients are supported.");

        foreach (var raw in list.Take(NotificationLimits.MaxRecipients))
        {
            if (!TryNormalizeMailbox(raw, out var address, out var key))
            {
                errors.Add("recipients", $"'{Truncate(raw)}' is not a valid mailbox address.");
                continue;
            }

            if (!seen.Add(key))
                errors.Add("recipients", $"'{address}' is listed more than once.");
            else
                normalized.Add((address, key));
        }

        return errors;
    }

    public static NotificationValidationErrors ValidateSmtp(NotificationSmtpSettings smtp, bool hasPassword)
    {
        var errors = new NotificationValidationErrors();

        if (string.IsNullOrWhiteSpace(smtp.Host) || !HostPattern().IsMatch(smtp.Host.Trim()))
            errors.Add("host", "Enter a host name or address without a scheme or path.");
        if (smtp.Port is < 1 or > 65535)
            errors.Add("port", "Port must be between 1 and 65535.");
        if (smtp.TlsMode is not ("starttls" or "tls"))
            errors.Add("tlsMode", "Choose required STARTTLS or TLS on connect. Unencrypted SMTP is not supported.");
        if (smtp.AuthMode is not ("none" or "password"))
            errors.Add("authMode", "Choose no authentication or username and password.");

        if (smtp.AuthMode == "password")
        {
            if (string.IsNullOrEmpty(smtp.Username) || HasControlCharacters(smtp.Username) || smtp.Username.Length > NotificationLimits.MaxFieldLength)
                errors.Add("username", "Enter the SMTP username.");
            if (!hasPassword)
                errors.Add("password", "Enter the SMTP password.");
        }

        if (!TryNormalizeMailbox(smtp.SenderAddress, out _, out _))
            errors.Add("senderAddress", "Enter a valid sender mailbox address.");
        if (smtp.SenderName is { } name && (HasControlCharacters(name) || name.Length > 100))
            errors.Add("senderName", "Sender name must be plain text up to 100 characters.");

        return errors;
    }

    public static NotificationValidationErrors ValidateMatrix(NotificationMatrixSettings matrix, bool hasToken, bool insecureHttpPermitted)
    {
        var errors = new NotificationValidationErrors();

        if (!Uri.TryCreate(matrix.HomeserverUrl, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            errors.Add("homeserverUrl", "Enter the homeserver base URL, for example https://matrix.example.org.");
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            if (!(uri.Scheme == Uri.UriSchemeHttp && matrix.AllowInsecureHttp && insecureHttpPermitted))
                errors.Add("homeserverUrl", "The homeserver must use HTTPS.");
        }

        if (string.IsNullOrWhiteSpace(matrix.RoomId) || !MatrixRoomIdPattern().IsMatch(matrix.RoomId))
            errors.Add("roomId", "Enter the room ID, which starts with ! (for example !abc123:example.org), not a #alias.");
        if (!hasToken)
            errors.Add("accessToken", "Enter the bot access token.");
        if (matrix.AllowInsecureHttp && !insecureHttpPermitted)
            errors.Add("allowInsecureHttp", "Plain HTTP is only available in an isolated lab configuration.");

        return errors;
    }

    public static NotificationValidationErrors ValidatePolicy(NotificationSettings s)
    {
        var errors = new NotificationValidationErrors();

        static void Range(NotificationValidationErrors e, string field, int value, int min, int max)
        {
            if (value < min || value > max)
                e.Add(field, $"Must be between {min} and {max}.");
        }

        Range(errors, "failureDelayMinutes", s.FailureDelayMinutes, 0, 1440);
        Range(errors, "overdueGraceMinutes", s.OverdueGraceMinutes, 0, 1440);
        Range(errors, "reminderIntervalHours", s.ReminderIntervalHours, 1, 168);
        Range(errors, "coverageWarnHours", s.CoverageWarnHours, 1, 168);
        Range(errors, "coverageRecoverHours", s.CoverageRecoverHours, 1, 168);
        Range(errors, "coverageWarnPercent", s.CoverageWarnPercent, 1, 100);
        Range(errors, "coverageRecoverPercent", s.CoverageRecoverPercent, 1, 100);
        Range(errors, "coverageGapMinutes", s.CoverageGapMinutes, 0, 120);
        Range(errors, "retentionDays", s.RetentionDays, 1, 365);

        if (s.CoverageRecoverHours < s.CoverageWarnHours)
            errors.Add("coverageRecoverHours", "The recovery horizon must be at least the warning horizon.");
        if (s.CoverageRecoverPercent < s.CoverageWarnPercent)
            errors.Add("coverageRecoverPercent", "The recovery percentage must be at least the warning percentage.");

        return errors;
    }

    private static string Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= 40 ? value : value[..40] + "…";
}
