using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

[TestClass]
public sealed class NotificationValidationTests
{
    [TestMethod]
    [DataRow("admin@example.com", true)]
    [DataRow("Admin.Name+tag@sub.example.com", true)]
    [DataRow("Bob <bob@example.com>", false)]
    [DataRow("bob@example.com\r\nBcc: x@y.z", false)]
    [DataRow("a@b@c.com", false)]
    [DataRow("no-at-sign", false)]
    [DataRow("a@b.com, c@d.com", false)]
    [DataRow("", false)]
    public void Mailbox_AcceptsOnlyBareAddresses(string input, bool valid)
        => Assert.AreEqual(valid, NotificationValidation.TryNormalizeMailbox(input, out _, out _));

    [TestMethod]
    public void Mailbox_PreservesLocalPartCase_ButCanonicalizesDomain()
    {
        Assert.IsTrue(NotificationValidation.TryNormalizeMailbox("Admin@Example.COM", out var address, out var key));
        Assert.AreEqual("Admin@Example.COM", address);
        Assert.AreEqual("Admin@example.com", key);
    }

    [TestMethod]
    public void Recipients_RequireOneToTenUniqueAddresses()
    {
        Assert.IsFalse(NotificationValidation.ValidateRecipients([], out _).IsValid);
        Assert.IsFalse(NotificationValidation.ValidateRecipients(["a@x.com", "A@X.COM".Replace("A@", "a@")], out _).IsValid, "Same canonical mailbox twice.");
        Assert.IsTrue(NotificationValidation.ValidateRecipients(["a@x.com", "A@x.com"], out var kept).IsValid, "Case-sensitive local parts stay distinct.");
        Assert.HasCount(2, kept);
        Assert.IsFalse(NotificationValidation.ValidateRecipients(Enumerable.Range(0, 11).Select(i => $"u{i}@x.com"), out _).IsValid);
    }

    [TestMethod]
    public void Smtp_RequiresTlsAndCredentialsWhenAuthenticating()
    {
        var smtp = new NotificationSmtpSettings
        {
            Host = "smtp.example.com", Port = 587, TlsMode = "starttls", AuthMode = "password",
            Username = "u", SenderAddress = "m3undle@example.com",
        };
        Assert.IsTrue(NotificationValidation.ValidateSmtp(smtp, hasPassword: true).IsValid);
        Assert.IsFalse(NotificationValidation.ValidateSmtp(smtp, hasPassword: false).IsValid);

        smtp.AuthMode = "none";
        Assert.IsTrue(NotificationValidation.ValidateSmtp(smtp, hasPassword: false).IsValid, "A trusted relay needs no credentials.");

        smtp.TlsMode = "none";
        Assert.IsFalse(NotificationValidation.ValidateSmtp(smtp, true).IsValid, "No plaintext mode.");
        smtp.TlsMode = "starttls";
        smtp.Host = "https://smtp.example.com/";
        Assert.IsFalse(NotificationValidation.ValidateSmtp(smtp, true).IsValid);
    }

    [TestMethod]
    public void Matrix_RequiresHttps_UnlessLabHttpIsExplicitlyPermitted()
    {
        var matrix = new NotificationMatrixSettings { HomeserverUrl = "http://hs.lab:8008", RoomId = "!abc:hs.lab", AllowInsecureHttp = true };
        Assert.IsFalse(NotificationValidation.ValidateMatrix(matrix, true, insecureHttpPermitted: false).IsValid);
        Assert.IsTrue(NotificationValidation.ValidateMatrix(matrix, true, insecureHttpPermitted: true).IsValid);

        matrix.AllowInsecureHttp = false;
        Assert.IsFalse(NotificationValidation.ValidateMatrix(matrix, true, insecureHttpPermitted: true).IsValid, "Lab HTTP also needs the per-setup opt-in.");

        matrix.HomeserverUrl = "https://matrix.example.org";
        Assert.IsTrue(NotificationValidation.ValidateMatrix(matrix, true, false).IsValid);
        Assert.IsFalse(NotificationValidation.ValidateMatrix(matrix, hasToken: false, false).IsValid);
        matrix.RoomId = "#alias:example.org";
        Assert.IsFalse(NotificationValidation.ValidateMatrix(matrix, true, false).IsValid, "A room ID, not an alias.");
    }

    [TestMethod]
    [DataRow("!abc123:example.org", true)]
    [DataRow("!sQ4HTH-4rH2KIx-ksHkdpIiIvmQZ5dvZvSGzKEOd5yA", true)]
    [DataRow("!abc:matrix.example.org:8448", true)]
    [DataRow("#alias:example.org", false)]
    [DataRow("!", false)]
    [DataRow("!has space:example.org", false)]
    [DataRow("abc:example.org", false)]
    [DataRow("", false)]
    public void Matrix_AcceptsOldAndNewStyleRoomIds_ButNeverAnAlias(string roomId, bool valid)
    {
        var matrix = new NotificationMatrixSettings { HomeserverUrl = "https://matrix.example.org", RoomId = roomId };
        Assert.AreEqual(valid, NotificationValidation.ValidateMatrix(matrix, hasToken: true, insecureHttpPermitted: false).IsValid);
    }

    [TestMethod]
    public void Policy_ValidatesRangesAndCoupledFieldsAtomically()
    {
        Assert.IsTrue(NotificationValidation.ValidatePolicy(new NotificationSettings()).IsValid);
        Assert.IsFalse(NotificationValidation.ValidatePolicy(new NotificationSettings { FailureDelayMinutes = 1441 }).IsValid);
        Assert.IsFalse(NotificationValidation.ValidatePolicy(new NotificationSettings { ReminderIntervalHours = 0 }).IsValid);
        Assert.IsFalse(NotificationValidation.ValidatePolicy(new NotificationSettings { CoverageWarnHours = 24, CoverageRecoverHours = 12 }).IsValid);
        Assert.IsFalse(NotificationValidation.ValidatePolicy(new NotificationSettings { CoverageWarnPercent = 95, CoverageRecoverPercent = 90 }).IsValid);
        Assert.IsFalse(NotificationValidation.ValidatePolicy(new NotificationSettings { CoverageGapMinutes = 121 }).IsValid);
        Assert.IsFalse(NotificationValidation.ValidatePolicy(new NotificationSettings { RetentionDays = 366 }).IsValid);
    }
}
