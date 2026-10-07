using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Notifications.Providers;

/// <summary>
/// Matrix Client-Server adapter: a bot posts plain-text <c>m.notice</c> events into one private, unencrypted room. It is send
/// only — no room creation, linking, inbound commands, encryption or edits.
///
/// Safety properties: HTTPS unless an isolated lab explicitly opts in at both the runtime and the setup; redirects are
/// refused (the HTTP client follows none), so the bearer token can never be sent to a redirect target; the room is
/// re-checked for encryption immediately before every send; and the transaction ID is derived from the delivery plus the
/// room, device and payload, so a retry is deduplicated by the homeserver while a changed room, device or payload can never
/// reuse an ID for a different request.
/// </summary>
public sealed class MatrixNotificationProvider(
    IHttpClientFactory httpClientFactory,
    NotificationRuntimeOptions runtimeOptions,
    ILogger<MatrixNotificationProvider> logger) : INotificationProvider, INotificationConnectionValidator
{
    public const string HttpClientName = "matrix";
    private const int MaxBodyLength = 4_000;
    private static readonly TimeSpan MaxHonouredRetryAfter = TimeSpan.FromHours(6);

    public string Kind => NotificationProviderKinds.Matrix;

    // The homeserver deduplicates by transaction ID under a preserved room and device, so re-sending is safe.
    public bool SupportsIdempotentRetry => true;

    public async Task<NotificationSendResult> SendAsync(NotificationSendRequest request, CancellationToken cancellationToken)
    {
        if (request.Configuration is not MatrixProviderConfiguration config)
            return NotificationSendResult.Permanent("configuration", "The Matrix configuration is not valid.");
        if (!TryResolveBase(config, out var baseUri, out var failure))
            return failure!;
        if (string.IsNullOrWhiteSpace(config.DeviceId))
            return NotificationSendResult.Permanent("configuration", "The Matrix configuration has not been tested yet.");

        var encryption = await CheckRoomStateAsync(baseUri, config, cancellationToken);
        if (encryption is not null)
            return encryption;

        var body = request.Message.Title + "\n\n" + request.Message.Body;
        if (!string.IsNullOrWhiteSpace(request.Message.LinkUrl))
            body += "\n\n" + request.Message.LinkUrl;
        body = body.Length <= MaxBodyLength ? body : body[..MaxBodyLength];

        var transactionId = TransactionId(request, config, body);
        var uri = new Uri(baseUri, $"_matrix/client/v3/rooms/{Uri.EscapeDataString(config.RoomId)}/send/m.room.message/{Uri.EscapeDataString(transactionId)}");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { msgtype = "m.notice", body }), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken.Reveal());

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(httpRequest, cancellationToken);
            return await ClassifyAsync(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogDebug(ex, "Matrix send failed in transport.");
            // The transaction ID makes the retry idempotent, so an unknown outcome is safe to retry.
            return NotificationSendResult.Retryable("network", "The homeserver could not be reached.");
        }
    }

    /// <summary>
    /// Confirms the bot's identity and device, that it is joined to the room and may post there, and that the room is not
    /// encrypted. The discovered device is what transaction IDs are scoped to.
    /// </summary>
    public async Task<NotificationConnectionCheck> CheckAsync(NotificationProviderConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration is not MatrixProviderConfiguration config)
            return new(false, "configuration", "The Matrix configuration is not valid.");
        if (!TryResolveBase(config, out var baseUri, out var failure))
            return new(false, failure!.ErrorCode, failure.ErrorText);

        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);

            var whoami = await GetJsonAsync(client, baseUri, config, "_matrix/client/v3/account/whoami", cancellationToken);
            if (whoami.Failure is not null)
                return new(false, whoami.Failure.ErrorCode, whoami.Failure.ErrorText);
            var userId = ReadString(whoami.Json, "user_id");
            var deviceId = ReadString(whoami.Json, "device_id");
            if (userId is null)
                return new(false, "malformed_response", "The homeserver returned an unexpected identity response.");
            if (deviceId is null)
                return new(false, "no_device", "The access token is not tied to a device. Use a token from a normal login so messages can be deduplicated.");

            var joined = await GetJsonAsync(client, baseUri, config, "_matrix/client/v3/joined_rooms", cancellationToken);
            if (joined.Failure is not null)
                return new(false, joined.Failure.ErrorCode, joined.Failure.ErrorText);
            var isMember = joined.Json is { } j && j.TryGetProperty("joined_rooms", out var rooms) && rooms.ValueKind == JsonValueKind.Array
                && rooms.EnumerateArray().Any(r => string.Equals(r.GetString(), config.RoomId, StringComparison.Ordinal));
            if (!isMember)
                return new(false, "not_in_room", "The bot account has not joined this room. Invite it and accept the invitation.");

            var encrypted = await CheckRoomStateAsync(baseUri, config, cancellationToken);
            if (encrypted is not null)
                return new(false, encrypted.ErrorCode, encrypted.ErrorText);

            var power = await GetJsonAsync(client, baseUri, config, RoomState(config, "m.room.power_levels"), cancellationToken, allowNotFound: true);
            if (power.Failure is not null)
                return new(false, power.Failure.ErrorCode, power.Failure.ErrorText);
            if (!CanPost(power.Json, userId))
                return new(false, "no_permission", "The bot account is not allowed to post messages in this room.");

            return new(true, Discovered: new Dictionary<string, string> { ["user_id"] = userId, ["device_id"] = deviceId });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogDebug(ex, "Matrix connection check failed in transport.");
            return new(false, "network", "The homeserver could not be reached.");
        }
    }

    private async Task<NotificationSendResult?> CheckRoomStateAsync(Uri baseUri, MatrixProviderConfiguration config, CancellationToken cancellationToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = Authorized(HttpMethod.Get, new Uri(baseUri, RoomState(config, "m.room.encryption")), config);
            using var response = await client.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null; // No encryption state event: the room is not encrypted.
            if (response.IsSuccessStatusCode)
                return NotificationSendResult.Permanent("room_encrypted", "The room is encrypted. Notifications are plain text, so use an unencrypted private room.");
            return await ClassifyAsync(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            logger.LogDebug(ex, "Matrix room-state check failed in transport.");
            return NotificationSendResult.Retryable("network", "The homeserver could not be reached.");
        }
    }

    private bool TryResolveBase(MatrixProviderConfiguration config, out Uri baseUri, out NotificationSendResult? failure)
    {
        failure = null;
        baseUri = null!;
        if (!Uri.TryCreate(config.HomeserverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.UserInfo))
        {
            failure = NotificationSendResult.Permanent("configuration", "The homeserver address is not valid.");
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttps
            && !(parsed.Scheme == Uri.UriSchemeHttp && config.AllowInsecureHttp && runtimeOptions.AllowInsecureMatrixHttp))
        {
            failure = NotificationSendResult.Permanent("insecure_http", "The homeserver must use HTTPS.");
            return false;
        }

        baseUri = parsed;
        return true;
    }

    private static string RoomState(MatrixProviderConfiguration config, string eventType) =>
        $"_matrix/client/v3/rooms/{Uri.EscapeDataString(config.RoomId)}/state/{Uri.EscapeDataString(eventType)}";

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, MatrixProviderConfiguration config)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.AccessToken.Reveal());
        return request;
    }

    private async Task<(JsonElement? Json, NotificationSendResult? Failure)> GetJsonAsync(
        HttpClient client, Uri baseUri, MatrixProviderConfiguration config, string path, CancellationToken cancellationToken, bool allowNotFound = false)
    {
        using var request = Authorized(HttpMethod.Get, new Uri(baseUri, path), config);
        using var response = await client.SendAsync(request, cancellationToken);
        if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
            return (null, null);
        if (!response.IsSuccessStatusCode)
            return (null, await ClassifyAsync(response, cancellationToken));

        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return (document.RootElement.Clone(), null);
        }
        catch (JsonException)
        {
            return (null, NotificationSendResult.Retryable("malformed_response", "The homeserver returned an unreadable response."));
        }
    }

    private static bool CanPost(JsonElement? powerLevels, string userId)
    {
        // Without a power-levels event the room defaults apply and members may post.
        if (powerLevels is not { } levels || levels.ValueKind != JsonValueKind.Object)
            return true;

        var userLevel = levels.TryGetProperty("users", out var users) && users.TryGetProperty(userId, out var own) && own.TryGetInt64(out var u)
            ? u
            : levels.TryGetProperty("users_default", out var usersDefault) && usersDefault.TryGetInt64(out var ud) ? ud : 0L;
        var required = levels.TryGetProperty("events", out var events) && events.TryGetProperty("m.room.message", out var message) && message.TryGetInt64(out var m)
            ? m
            : levels.TryGetProperty("events_default", out var eventsDefault) && eventsDefault.TryGetInt64(out var ed) ? ed : 0L;
        return userLevel >= required;
    }

    private static string TransactionId(NotificationSendRequest request, MatrixProviderConfiguration config, string body)
    {
        var scope = $"{config.RoomId}\n{config.DeviceId}\n{request.Message.Title}\n{body}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)))[..16].ToLowerInvariant();
        return $"m3u-{request.DeliveryId}-{hash}";
    }

    private async Task<NotificationSendResult> ClassifyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            try
            {
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                var eventId = ReadString(document.RootElement, "event_id");
                if (!string.IsNullOrWhiteSpace(eventId) && eventId.Length <= 255 && !eventId.Any(char.IsControl))
                    return NotificationSendResult.Accepted(eventId);
            }
            catch (JsonException)
            {
            }

            return NotificationSendResult.Retryable("malformed_response", "The homeserver accepted the request but returned an unreadable event ID.");
        }

        if (status is >= 300 and < 400)
            return NotificationSendResult.Permanent("redirect_refused", "The homeserver redirected the request, which is not followed because it carries credentials.");

        var (errcode, retryAfterMs) = await ReadErrorAsync(response, cancellationToken);

        if (status == 429 || errcode == "M_LIMIT_EXCEEDED")
        {
            var delay = RetryAfter(response, retryAfterMs);
            return NotificationSendResult.Retryable("rate_limited", "The homeserver is rate limiting this account.", delay);
        }

        if (status >= 500)
            return NotificationSendResult.Retryable("server_error", $"The homeserver reported an error (HTTP {status}).");

        return (status, errcode) switch
        {
            (401, _) or (_, "M_UNKNOWN_TOKEN") or (_, "M_MISSING_TOKEN") =>
                NotificationSendResult.Permanent("auth_failed", "The homeserver rejected the access token."),
            (403, _) =>
                NotificationSendResult.Permanent("permission_denied", "The bot account is not allowed to post in this room."),
            (404, _) =>
                NotificationSendResult.Permanent("room_not_found", "The room was not found or the bot account has not joined it."),
            _ => NotificationSendResult.Permanent("rejected", $"The homeserver rejected the request (HTTP {status})."),
        };
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response, long? bodyMs)
    {
        TimeSpan? header = null;
        var value = response.Headers.RetryAfter;
        if (value?.Delta is { } delta)
            header = delta;
        else if (value?.Date is { } date)
            header = date - DateTimeOffset.UtcNow;

        // Retry-After is the current mechanism; retry_after_ms is the legacy body field.
        var guided = header ?? (bodyMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null);
        if (guided is null)
            return null;
        return guided < TimeSpan.Zero ? TimeSpan.Zero : guided > MaxHonouredRetryAfter ? MaxHonouredRetryAfter : guided;
    }

    private static async Task<(string? Errcode, long? RetryAfterMs)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var errcode = ReadString(document.RootElement, "errcode");
            long? ms = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("retry_after_ms", out var element) && element.TryGetInt64(out var value)
                ? value
                : null;
            return (errcode, ms);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
