using M3Undle.Web.Application;
using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Application.Notifications.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class MatrixNotificationProviderTests
{
    private sealed class LabOptions(bool allowHttp) : NotificationRuntimeOptions(new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance))
    {
        public override bool AllowInsecureMatrixHttp => allowHttp;
    }

    // Uses the production HTTP client registration (no redirects), with plain HTTP allowed only as an isolated lab would.
    private static MatrixNotificationProvider NewProvider(bool runtimeAllowsHttp = true)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMatrixHttpClient();
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return new MatrixNotificationProvider(factory, new LabOptions(runtimeAllowsHttp), NullLogger<MatrixNotificationProvider>.Instance);
    }

    private static MatrixProviderConfiguration Config(FakeMatrixHomeserver server, string token = FakeMatrixHomeserver.Token,
        string? device = "DEV1", bool allowHttp = true, string room = FakeMatrixHomeserver.RoomId) =>
        new(server.BaseUrl, room, new NotificationSecret(token), "@bot:test", device, allowHttp);

    private static NotificationSendRequest Request(MatrixProviderConfiguration config, string id = "delivery-1", string body = "Guide source failing", string? link = null) =>
        new(id, $"<{id}@m3undle.local>", new NotificationMessage("Guide source is failing", body, "Warning", link, new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc)),
            new NotificationTarget("matrix-room", "Matrix room"), 1, config);

    // ---------------------------------------------------------------- connection check

    [TestMethod]
    public async Task Check_DiscoversTheBotIdentityAndDevice_WhenEverythingIsInOrder()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        var check = await NewProvider().CheckAsync(Config(server), CancellationToken.None);

        Assert.IsTrue(check.Succeeded, check.ErrorText);
        Assert.AreEqual("DEV1", check.Discovered!["device_id"]);
        Assert.AreEqual("@bot:test", check.Discovered["user_id"]);
    }

    [TestMethod]
    public async Task Check_RejectsEncryptedRooms_MissingMembership_MissingPermission_BadTokens_AndTokensWithoutDevices()
    {
        var provider = NewProvider();

        await using (var server = await FakeMatrixHomeserver.StartAsync())
        {
            server.Encrypted = true;
            Assert.AreEqual("room_encrypted", (await provider.CheckAsync(Config(server), CancellationToken.None)).ErrorCode);
        }

        await using (var server = await FakeMatrixHomeserver.StartAsync())
        {
            server.Joined = false;
            Assert.AreEqual("not_in_room", (await provider.CheckAsync(Config(server), CancellationToken.None)).ErrorCode);
        }

        await using (var server = await FakeMatrixHomeserver.StartAsync())
        {
            server.PowerLevelsJson = "{\"users\":{\"@bot:test\":0},\"events\":{\"m.room.message\":50},\"users_default\":0}";
            Assert.AreEqual("no_permission", (await provider.CheckAsync(Config(server), CancellationToken.None)).ErrorCode);
            server.PowerLevelsJson = "{\"users\":{\"@bot:test\":100},\"events\":{\"m.room.message\":50}}";
            Assert.IsTrue((await provider.CheckAsync(Config(server), CancellationToken.None)).Succeeded, "A sufficient power level may post.");
        }

        await using (var server = await FakeMatrixHomeserver.StartAsync())
        {
            Assert.AreEqual("auth_failed", (await provider.CheckAsync(Config(server, token: "wrong"), CancellationToken.None)).ErrorCode);
            server.DeviceId = null;
            Assert.AreEqual("no_device", (await provider.CheckAsync(Config(server), CancellationToken.None)).ErrorCode);
        }
    }

    [TestMethod]
    public async Task PlainHttp_IsRefusedUnlessBothTheRuntimeAndTheSetupOptIn()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();

        var production = NewProvider(runtimeAllowsHttp: false);
        Assert.AreEqual("insecure_http", (await production.CheckAsync(Config(server, allowHttp: true), CancellationToken.None)).ErrorCode,
            "A setup flag alone cannot enable plain HTTP in a production runtime.");
        Assert.AreEqual("insecure_http", (await production.SendAsync(Request(Config(server)), CancellationToken.None)).ErrorCode);

        var lab = NewProvider(runtimeAllowsHttp: true);
        Assert.AreEqual("insecure_http", (await lab.CheckAsync(Config(server, allowHttp: false), CancellationToken.None)).ErrorCode,
            "The lab runtime alone does not opt a setup in.");
        Assert.IsTrue((await lab.CheckAsync(Config(server, allowHttp: true), CancellationToken.None)).Succeeded);
    }

    // ---------------------------------------------------------------- sending

    [TestMethod]
    public async Task Send_PostsAPlainTextNotice_WithABearerToken_AndReturnsTheEventId()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        var result = await NewProvider().SendAsync(Request(Config(server), link: "https://m3undle.example/epg"), CancellationToken.None);

        Assert.AreEqual(NotificationSendOutcome.Accepted, result.Outcome, result.ErrorText);
        Assert.AreEqual("$event1", result.RemoteReference);

        using var sent = System.Text.Json.JsonDocument.Parse(server.SentBodies.Single());
        Assert.AreEqual("m.notice", sent.RootElement.GetProperty("msgtype").GetString(), "Notices do not trigger bot loops.");
        Assert.IsFalse(sent.RootElement.TryGetProperty("formatted_body", out _), "Plain text only.");
        StringAssert.Contains(sent.RootElement.GetProperty("body").GetString(), "Guide source is failing");
        StringAssert.Contains(sent.RootElement.GetProperty("body").GetString(), "https://m3undle.example/epg");
        Assert.IsTrue(server.Requests.Where(r => r.Method == "PUT").All(r => r.Authorization == $"Bearer {FakeMatrixHomeserver.Token}"));
    }

    [TestMethod]
    public async Task ARetryOfTheSameDelivery_IsDeduplicatedByTheHomeserver_ButAChangedRoomDeviceOrPayloadIsNot()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        var provider = NewProvider();

        var first = await provider.SendAsync(Request(Config(server)), CancellationToken.None);
        var retry = await provider.SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(first.RemoteReference, retry.RemoteReference);
        Assert.AreEqual(1, server.DistinctEvents, "The same delivery, payload, room and device reuses its transaction ID.");

        await provider.SendAsync(Request(Config(server), body: "A different payload"), CancellationToken.None);
        Assert.AreEqual(2, server.DistinctEvents, "A changed payload never reuses a transaction ID.");

        await provider.SendAsync(Request(Config(server, device: "DEV2")), CancellationToken.None);
        Assert.AreEqual(3, server.DistinctEvents, "A changed device never reuses a transaction ID.");

        await provider.SendAsync(Request(Config(server), id: "delivery-2"), CancellationToken.None);
        Assert.AreEqual(4, server.DistinctEvents, "A different delivery is a different message.");
    }

    [TestMethod]
    public async Task RateLimits_HonourRetryAfter_FallBackToTheLegacyBodyField_AndNeverExceedTheCap()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        var provider = NewProvider();

        server.NextSendStatus(429, "{\"errcode\":\"M_LIMIT_EXCEEDED\",\"retry_after_ms\":9000}", retryAfterHeader: "5");
        var header = await provider.SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.RetryableFailure, header.Outcome);
        Assert.AreEqual("rate_limited", header.ErrorCode);
        Assert.AreEqual(TimeSpan.FromSeconds(5), header.RetryAfter, "Retry-After is preferred.");

        server.NextSendStatus(429, "{\"errcode\":\"M_LIMIT_EXCEEDED\",\"retry_after_ms\":2500}");
        var legacy = await provider.SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2500), legacy.RetryAfter);

        server.NextSendStatus(429, "{}", retryAfterHeader: "999999");
        var huge = await provider.SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromHours(6), huge.RetryAfter, "A hostile or buggy delay is capped.");
    }

    [TestMethod]
    [DataRow(500, "server_error", NotificationSendOutcome.RetryableFailure)]
    [DataRow(503, "server_error", NotificationSendOutcome.RetryableFailure)]
    [DataRow(401, "auth_failed", NotificationSendOutcome.PermanentFailure)]
    [DataRow(403, "permission_denied", NotificationSendOutcome.PermanentFailure)]
    [DataRow(404, "room_not_found", NotificationSendOutcome.PermanentFailure)]
    [DataRow(400, "rejected", NotificationSendOutcome.PermanentFailure)]
    public async Task HttpFailures_AreClassifiedAsRetryableOrConfiguration(int status, string code, NotificationSendOutcome expected)
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        server.NextSendStatus(status);
        var result = await NewProvider().SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(code, result.ErrorCode);
    }

    [TestMethod]
    public async Task MalformedResponses_AreRetriedSafely_BecauseTheTransactionIsIdempotent()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        var provider = NewProvider();

        server.NextSendStatus(200, "not json at all");
        Assert.AreEqual("malformed_response", (await provider.SendAsync(Request(Config(server)), CancellationToken.None)).ErrorCode);

        server.NextSendStatus(200, "{\"something\":\"else\"}");
        var missingId = await provider.SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.RetryableFailure, missingId.Outcome);

        server.NextSendStatus(200, "{\"event_id\":\"" + new string('x', 400) + "\"}");
        Assert.AreEqual(NotificationSendOutcome.RetryableFailure, (await provider.SendAsync(Request(Config(server)), CancellationToken.None)).Outcome,
            "An implausible event ID is not accepted.");
    }

    [TestMethod]
    public async Task Redirects_AreRefused_AndTheBearerTokenNeverReachesTheRedirectTarget()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        server.NextSend(async c =>
        {
            c.Response.StatusCode = 302;
            c.Response.Headers.Location = "/elsewhere";
            await Task.CompletedTask;
            return true;
        });

        var result = await NewProvider().SendAsync(Request(Config(server)), CancellationToken.None);

        Assert.AreEqual(NotificationSendOutcome.PermanentFailure, result.Outcome);
        Assert.AreEqual("redirect_refused", result.ErrorCode);
        Assert.IsEmpty(server.RedirectTargetHits, "The client must not follow the redirect with credentials.");
    }

    [TestMethod]
    public async Task EncryptionEnabledAfterTheTest_BlocksPlainTextAlerts_AtSendTime()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        var provider = NewProvider();
        Assert.IsTrue((await provider.CheckAsync(Config(server), CancellationToken.None)).Succeeded);

        server.Encrypted = true;
        var result = await provider.SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.PermanentFailure, result.Outcome);
        Assert.AreEqual("room_encrypted", result.ErrorCode);
        Assert.IsEmpty(server.SentBodies, "Nothing is sent into a room that became encrypted.");
    }

    [TestMethod]
    public async Task UntestedConfiguration_AndWrongTypes_AreConfigurationErrors_AndHostCancellationPropagates()
    {
        await using var server = await FakeMatrixHomeserver.StartAsync();
        var provider = NewProvider();

        Assert.AreEqual("configuration", (await provider.SendAsync(Request(Config(server, device: null)), CancellationToken.None)).ErrorCode,
            "The device is resolved by a test; an untested setup cannot send.");
        Assert.AreEqual("configuration", (await provider.SendAsync(
            Request(Config(server)) with { Configuration = new SmtpProviderConfiguration("h", 1, "tls", "none", null, null, "a@b.c", null) }, CancellationToken.None)).ErrorCode);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.SendAsync(Request(Config(server)), cts.Token));
    }

    [TestMethod]
    public async Task UnreachableHomeserver_IsARetryableNetworkFailure()
    {
        var server = await FakeMatrixHomeserver.StartAsync();
        var config = Config(server);
        await server.DisposeAsync();

        var result = await NewProvider().SendAsync(Request(config), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.RetryableFailure, result.Outcome);
        Assert.AreEqual("network", result.ErrorCode);
    }

    [TestMethod]
    public void Matrix_DeclaresIdempotentRetry_SmtpDoesNot()
    {
        Assert.IsTrue(NewProvider().SupportsIdempotentRetry);
        Assert.IsFalse(((INotificationProvider)new SmtpNotificationProvider(NullLogger<SmtpNotificationProvider>.Instance)).SupportsIdempotentRetry);
    }
}
