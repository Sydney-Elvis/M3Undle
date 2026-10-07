using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Application.Notifications.Providers;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class SmtpNotificationProviderTests
{
    private static SmtpProviderConfiguration Config(FakeSmtpServer server, string tlsMode = "starttls", string auth = "password", string password = "p@ss word ") =>
        new("127.0.0.1", server.Port, tlsMode, auth, "mailer", auth == "password" ? new NotificationSecret(password) : null, "m3undle@example.org", "M3Undle");

    private static NotificationSendRequest Request(SmtpProviderConfiguration config, string recipient = "admin@example.org", string title = "Guide source is failing", string? link = "https://m3undle.example/epg") =>
        new("delivery-1", "<delivery-1@m3undle.local>",
            new NotificationMessage(title, "The source has failed since 2026-10-04.", "Warning", link, new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc)),
            new NotificationTarget("rcpt-1", recipient), 1, config);

    // The server's self-signed certificate stands in for a locally trusted test CA; everything else about validation is default.
    private static SmtpNotificationProvider Trusting(FakeSmtpServer server) => new(NullLogger<SmtpNotificationProvider>.Instance, () =>
    {
        var client = new SmtpClient { CheckCertificateRevocation = false };
        client.ServerCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == server.Certificate.GetCertHashString();
        return client;
    });

    private static SmtpNotificationProvider Default() => new(NullLogger<SmtpNotificationProvider>.Instance);

    [TestMethod]
    public async Task StartTls_AuthenticatesWithTheExactPassword_AndDeliversOneRecipientWithAStableMessageId()
    {
        await using var server = new FakeSmtpServer();
        var result = await Trusting(server).SendAsync(Request(Config(server)), CancellationToken.None);

        Assert.AreEqual(NotificationSendOutcome.Accepted, result.Outcome, result.ErrorText);
        StringAssert.Contains(result.RemoteReference, "queued as TEST123");
        Assert.IsFalse(server.CredentialsSeenOverPlaintext, "Credentials only ever travel over TLS.");
        Assert.AreEqual("mailer\np@ss word ", server.AuthAttempts.Single(), "The password is sent exactly, whitespace and symbols included.");

        var message = server.Messages.Single();
        CollectionAssert.AreEqual(new[] { "admin@example.org" }, message.Recipients.ToArray(), "One envelope recipient per delivery.");
        Assert.AreEqual("m3undle@example.org", message.From);
        StringAssert.Contains(message.Data, "Message-Id: <delivery-1@m3undle.local>");
        StringAssert.Contains(message.Data, "Auto-Submitted: auto-generated");
        StringAssert.Contains(message.Data, "Open M3Undle: https://m3undle.example/epg");
        StringAssert.Contains(message.Data, "not proof that anyone read it");
    }

    [TestMethod]
    public async Task TlsOnConnect_Works()
    {
        await using var server = new FakeSmtpServer { Tls = SmtpTlsMode.Implicit };
        var result = await Trusting(server).SendAsync(Request(Config(server, tlsMode: "tls")), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.Accepted, result.Outcome, result.ErrorText);
        Assert.IsFalse(server.CredentialsSeenOverPlaintext);
    }

    [TestMethod]
    public async Task TrustedRelayWithoutAuthentication_Works_AndSendsNoCredentials()
    {
        await using var server = new FakeSmtpServer { RequireAuth = false };
        var result = await Trusting(server).SendAsync(Request(Config(server, auth: "none")), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.Accepted, result.Outcome, result.ErrorText);
        Assert.IsEmpty(server.AuthAttempts);
    }

    [TestMethod]
    public async Task ServerWithoutStartTls_IsRefused_NeverDowngradedToPlaintext_AndNeverSeesCredentials()
    {
        await using var server = new FakeSmtpServer { AdvertiseStartTls = false };
        var result = await Trusting(server).SendAsync(Request(Config(server)), CancellationToken.None);

        Assert.AreEqual(NotificationSendOutcome.PermanentFailure, result.Outcome);
        Assert.AreEqual("tls_unsupported", result.ErrorCode);
        Assert.IsEmpty(server.AuthAttempts);
        Assert.IsFalse(server.CredentialsSeenOverPlaintext);
        Assert.IsEmpty(server.Messages);
    }

    [TestMethod]
    public async Task UntrustedCertificate_FailsNormalValidation_BeforeAnyCredentialsAreSent()
    {
        await using var server = new FakeSmtpServer();
        var result = await Default().SendAsync(Request(Config(server)), CancellationToken.None);

        Assert.AreEqual(NotificationSendOutcome.PermanentFailure, result.Outcome);
        Assert.AreEqual("tls_failed", result.ErrorCode);
        Assert.IsEmpty(server.AuthAttempts, "No certificate bypass: a failed handshake sends nothing.");
    }

    [TestMethod]
    public async Task AuthenticationRejection_IsAPermanentConfigurationFailure()
    {
        await using var server = new FakeSmtpServer();
        var result = await Trusting(server).SendAsync(Request(Config(server, password: "wrong")), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.PermanentFailure, result.Outcome);
        Assert.AreEqual("auth_failed", result.ErrorCode);
        Assert.IsEmpty(server.Messages);
    }

    [TestMethod]
    [DataRow("550 5.1.1 mailbox unavailable", NotificationSendOutcome.PermanentFailure, "smtp_rejected")]
    [DataRow("451 4.3.0 try again later", NotificationSendOutcome.RetryableFailure, "smtp_temporary")]
    public async Task RecipientRefusal_IsClassifiedByItsStatusClass(string reply, NotificationSendOutcome expected, string code)
    {
        await using var server = new FakeSmtpServer { RcptReply = reply };
        var result = await Trusting(server).SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(code, result.ErrorCode);
        Assert.IsEmpty(server.Messages);
    }

    [TestMethod]
    [DataRow("451 4.3.0 deferred", NotificationSendOutcome.RetryableFailure)]
    [DataRow("554 5.7.1 message refused", NotificationSendOutcome.PermanentFailure)]
    public async Task DefiniteFinalReplyRejection_IsNotAmbiguous(string reply, NotificationSendOutcome expected)
    {
        await using var server = new FakeSmtpServer { FinalReply = reply };
        var result = await Trusting(server).SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(expected, result.Outcome, "A definite 4xx/5xx after DATA means the server told us it did not accept the message.");
    }

    [TestMethod]
    public async Task ConnectionLostAfterTheBodyWasSent_IsUncertain_NotRetryable()
    {
        await using var server = new FakeSmtpServer { DropBeforeFinalReply = true };
        var result = await Trusting(server).SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.Uncertain, result.Outcome);
        Assert.AreEqual("ack_lost", result.ErrorCode);
    }

    [TestMethod]
    public async Task DisconnectAfterAcceptance_StaysAccepted()
    {
        await using var server = new FakeSmtpServer { CloseAfterFinalReply = true };
        var result = await Trusting(server).SendAsync(Request(Config(server)), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.Accepted, result.Outcome, "Cleanup trouble never undoes an accepted message.");
        Assert.HasCount(1, server.Messages);
    }

    [TestMethod]
    public async Task UnreachableServer_IsARetryableNetworkFailure()
    {
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        var config = new SmtpProviderConfiguration("127.0.0.1", port, "starttls", "none", null, null, "m3undle@example.org", null);
        var result = await Default().SendAsync(Request(config), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.RetryableFailure, result.Outcome);
        Assert.AreEqual("network", result.ErrorCode);
    }

    [TestMethod]
    public async Task CancellationBeforeTheBodyIsSent_IsACleanRetry_ButAfterwardsItIsUncertain()
    {
        await using (var server = new FakeSmtpServer { AuthReply = "235 ok" })
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var early = await Trusting(server).SendAsync(Request(Config(server)), cts.Token);
            Assert.AreEqual(NotificationSendOutcome.RetryableFailure, early.Outcome);
            Assert.AreEqual("cancelled", early.ErrorCode);
        }

        await using (var stalling = new FakeSmtpServer { StallBeforeFinalReply = true })
        {
            using var cts = new CancellationTokenSource();
            var pending = Trusting(stalling).SendAsync(Request(Config(stalling)), cts.Token);
            await stalling.DataReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cts.CancelAsync();
            var late = await pending;
            Assert.AreEqual(NotificationSendOutcome.Uncertain, late.Outcome, "Cancelled after the body was sent: acceptance is unknown.");
        }
    }

    [TestMethod]
    public async Task HeaderInjectionAndBadRecipients_AreNeutralised()
    {
        await using var server = new FakeSmtpServer();
        var provider = Trusting(server);

        var injected = await provider.SendAsync(Request(Config(server), title: "Hello\r\nBcc: evil@example.org"), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.Accepted, injected.Outcome, injected.ErrorText);
        var data = server.Messages.Single().Data;
        Assert.DoesNotContain("\r\nBcc:", data);
        Assert.DoesNotContain("evil@example.org", string.Join(",", server.Messages.Single().Recipients));

        var bad = await provider.SendAsync(Request(Config(server), recipient: "not an address"), CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.PermanentFailure, bad.Outcome);
        Assert.AreEqual("invalid_message", bad.ErrorCode);
    }

    [TestMethod]
    public async Task WrongConfigurationType_IsAPermanentConfigurationError()
    {
        var provider = Default();
        var wrong = new MatrixProviderConfiguration("https://m", "!r:m", new NotificationSecret("t"), null, "D", false);
        var result = await provider.SendAsync(Request(Config(new FakeSmtpServer { Tls = SmtpTlsMode.None }) with { Host = "x" }) with { Configuration = wrong }, CancellationToken.None);
        Assert.AreEqual(NotificationSendOutcome.PermanentFailure, result.Outcome);
        Assert.AreEqual("configuration", result.ErrorCode);
    }
}
