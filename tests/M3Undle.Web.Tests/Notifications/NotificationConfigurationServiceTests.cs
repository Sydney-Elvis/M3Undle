using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Notifications;

// Mutates process-wide M3UNDLE_ENCRYPTION_KEY(S), so it must not run alongside other tests (see SecretEncryptionServiceTests).
[TestClass]
[DoNotParallelize]
public sealed class NotificationConfigurationServiceTests
{
    private static string RandomKey() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private static SmtpSetupRequest Smtp(
        string? password = "p@ss word ", bool clear = false, string host = "smtp.example.org", string auth = "password",
        string[]? recipients = null, string sender = "m3undle@example.org") =>
        new(host, 587, "starttls", auth, "mailer", password, clear, sender, "M3Undle", recipients ?? ["a@example.org", "b@example.org"]);

    private static async Task<(NotificationLifecycleHarness Harness, EnvScope Env)> NewAsync()
    {
        var env = new EnvScope(RandomKey());
        var harness = await NotificationLifecycleHarness.CreateAsync(realAdapters: true);
        return (harness, env);
    }

    private static async Task<NotificationDestination> DestinationAsync(NotificationLifecycleHarness h, string kind)
    {
        await using var db = h.NewContext();
        return await db.NotificationDestinations.AsNoTracking().Include(d => d.Smtp).Include(d => d.Matrix).Include(d => d.Recipients)
            .SingleAsync(d => d.Kind == kind);
    }

    [TestMethod]
    public async Task SaveSmtp_StoresTheSecretEncryptedAndExact_AndNeverEnablesOrSends()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        var before = await DestinationAsync(h, NotificationProviderKinds.Smtp);

        var result = await h.Config.SaveSmtpAsync(Smtp(), before.ConfigRevision, CancellationToken.None);

        Assert.AreEqual(NotificationOperationStatus.Ok, result.Status, result.Message);
        var saved = await DestinationAsync(h, NotificationProviderKinds.Smtp);
        Assert.AreEqual(before.ConfigRevision + 1, saved.ConfigRevision);
        Assert.IsFalse(saved.Enabled, "Save never enables.");
        Assert.AreEqual(NotificationVerificationStates.Unverified, saved.VerificationStatus);
        Assert.IsTrue(saved.Smtp!.PasswordEncrypted!.StartsWith("m3e:v2:", StringComparison.Ordinal));
        Assert.AreEqual("p@ss word ", h.Encryption.Decrypt(saved.Smtp.PasswordEncrypted!), "Passwords are preserved exactly, including whitespace.");
        Assert.AreEqual(0, h.Smtp.Calls, "Saving never sends.");
        Assert.HasCount(2, saved.Recipients);
        Assert.DoesNotContain("p@ss", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [TestMethod]
    public async Task BlankPasswordKeeps_ExplicitClearRemoves_AndSamePasswordIsNotAChange()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        var rev = (await DestinationAsync(h, NotificationProviderKinds.Smtp)).ConfigRevision;
        await h.Config.SaveSmtpAsync(Smtp(), rev, CancellationToken.None);
        var first = await DestinationAsync(h, NotificationProviderKinds.Smtp);

        var keep = await h.Config.SaveSmtpAsync(Smtp(password: ""), first.ConfigRevision, CancellationToken.None);
        Assert.AreEqual("No changes.", keep.Message);
        Assert.AreEqual(first.Smtp!.PasswordEncrypted, (await DestinationAsync(h, NotificationProviderKinds.Smtp)).Smtp!.PasswordEncrypted, "Blank keeps the stored secret.");

        var same = await h.Config.SaveSmtpAsync(Smtp(password: "p@ss word "), first.ConfigRevision, CancellationToken.None);
        Assert.AreEqual("No changes.", same.Message, "Re-entering the same credential is not a credential change.");
        Assert.AreEqual(first.ConfigRevision, (await DestinationAsync(h, NotificationProviderKinds.Smtp)).ConfigRevision);

        var clear = await h.Config.SaveSmtpAsync(Smtp(password: null, clear: true), first.ConfigRevision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Invalid, clear.Status, "Password mode without a password is rejected rather than saved broken.");

        var relay = await h.Config.SaveSmtpAsync(Smtp(password: null, clear: true, auth: "none"), first.ConfigRevision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, relay.Status, relay.Message);
        var cleared = await DestinationAsync(h, NotificationProviderKinds.Smtp);
        Assert.IsNull(cleared.Smtp!.PasswordEncrypted);
        Assert.IsNull(cleared.Smtp.Username);
    }

    [TestMethod]
    public async Task IdentityChangesOnlyForRealDeliveryIdentity_NotForRecipientsOrReencryption()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        var d0 = await DestinationAsync(h, NotificationProviderKinds.Smtp);
        await h.Config.SaveSmtpAsync(Smtp(), d0.ConfigRevision, CancellationToken.None);
        var d1 = await DestinationAsync(h, NotificationProviderKinds.Smtp);
        Assert.AreEqual(d0.DeliveryIdentityRevision + 1, d1.DeliveryIdentityRevision, "First real setup is a new identity.");

        await h.Config.SaveSmtpAsync(Smtp(password: null, recipients: ["a@example.org", "c@example.org"]), d1.ConfigRevision, CancellationToken.None);
        var d2 = await DestinationAsync(h, NotificationProviderKinds.Smtp);
        Assert.AreEqual(d1.ConfigRevision + 1, d2.ConfigRevision, "A recipient change invalidates verification...");
        Assert.AreEqual(d1.DeliveryIdentityRevision, d2.DeliveryIdentityRevision, "...but is not a new setup identity, so existing recipients are not re-sent to.");
        var keptId = d1.Recipients.Single(r => r.Address == "a@example.org").RecipientId;
        Assert.AreEqual(keptId, d2.Recipients.Single(r => r.Address == "a@example.org").RecipientId, "A retained recipient keeps its ID.");
        CollectionAssert.DoesNotContain(d2.Recipients.Select(r => r.Address).ToArray(), "b@example.org");

        await h.Config.SaveSmtpAsync(Smtp(password: null, host: "smtp2.example.org", recipients: ["a@example.org", "c@example.org"]), d2.ConfigRevision, CancellationToken.None);
        var d3 = await DestinationAsync(h, NotificationProviderKinds.Smtp);
        Assert.AreEqual(d2.DeliveryIdentityRevision + 1, d3.DeliveryIdentityRevision, "A different host is a different identity.");
    }

    [TestMethod]
    public async Task StaleRevisionsConflict_AndInvalidInputIsRejectedWithFieldErrors()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        var rev = (await DestinationAsync(h, NotificationProviderKinds.Smtp)).ConfigRevision;
        await h.Config.SaveSmtpAsync(Smtp(), rev, CancellationToken.None);

        Assert.AreEqual(NotificationOperationStatus.Conflict, (await h.Config.SaveSmtpAsync(Smtp(host: "x.example.org"), rev, CancellationToken.None)).Status);

        var invalid = await h.Config.SaveSmtpAsync(Smtp(host: "https://bad/", recipients: ["nope"]), rev + 1, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Invalid, invalid.Status);
        Assert.IsTrue(invalid.Errors!.ContainsKey("host") && invalid.Errors.ContainsKey("recipients"));
    }

    [TestMethod]
    public async Task EnableNeedsAnExactRevisionVerification_TestAllTargets_AndPartialIsNotVerified()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        await h.Config.SaveSmtpAsync(Smtp(), (await DestinationAsync(h, NotificationProviderKinds.Smtp)).ConfigRevision, CancellationToken.None);
        var saved = await DestinationAsync(h, NotificationProviderKinds.Smtp);

        var early = await h.Config.SetDestinationEnabledAsync(NotificationProviderKinds.Smtp, true, saved.ConfigRevision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Conflict, early.Status, "Cannot enable before a successful test.");

        h.Smtp.Then(NotificationSendResult.Accepted()).Then(NotificationSendResult.Permanent("rejected", "mailbox unavailable"));
        var partial = await h.Config.TestAsync(NotificationProviderKinds.Smtp, saved.ConfigRevision, CancellationToken.None);
        Assert.AreEqual(2, h.Smtp.Calls, "Every configured target is tested.");
        Assert.AreEqual(NotificationVerificationStates.Partial, partial.VerificationStatus);
        Assert.IsFalse(partial.VerificationApplied);
        Assert.AreEqual(NotificationOperationStatus.Conflict,
            (await h.Config.SetDestinationEnabledAsync(NotificationProviderKinds.Smtp, true, saved.ConfigRevision, CancellationToken.None)).Status);

        h.Time.Advance(TimeSpan.FromMinutes(2));
        var ok = await h.Config.TestAsync(NotificationProviderKinds.Smtp, saved.ConfigRevision, CancellationToken.None);
        Assert.AreEqual(NotificationVerificationStates.Verified, ok.VerificationStatus);
        Assert.IsTrue(ok.VerificationApplied);
        StringAssert.Contains(ok.Summary, "not proof");

        await using (var db = h.NewContext())
        {
            var epochBefore = (await db.NotificationSettings.SingleAsync()).ActivationEpoch;
            Assert.AreEqual(NotificationOperationStatus.Ok,
                (await h.Config.SetDestinationEnabledAsync(NotificationProviderKinds.Smtp, true, saved.ConfigRevision, CancellationToken.None)).Status);
            Assert.IsTrue((await db.NotificationDestinations.AsNoTracking().SingleAsync(d => d.Kind == NotificationProviderKinds.Smtp)).Enabled);
            Assert.IsGreaterThan(epochBefore, (await db.NotificationSettings.AsNoTracking().SingleAsync()).ActivationEpoch, "Enabling starts a new activation epoch.");
        }
    }

    [TestMethod]
    public async Task ATestThatFinishesAfterAnEdit_CannotVerifyTheNewerRevision()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        await h.Config.SaveSmtpAsync(Smtp(), (await DestinationAsync(h, NotificationProviderKinds.Smtp)).ConfigRevision, CancellationToken.None);
        var saved = await DestinationAsync(h, NotificationProviderKinds.Smtp);

        h.Smtp.Then(async (_, _) =>
        {
            // The administrator edits the setup while the test is in flight.
            await h.Config.SaveSmtpAsync(Smtp(password: null, host: "other.example.org"), saved.ConfigRevision, CancellationToken.None);
            return NotificationSendResult.Accepted();
        }).Then(NotificationSendResult.Accepted());

        var result = await h.Config.TestAsync(NotificationProviderKinds.Smtp, saved.ConfigRevision, CancellationToken.None);

        Assert.IsFalse(result.VerificationApplied);
        var after = await DestinationAsync(h, NotificationProviderKinds.Smtp);
        Assert.IsNull(after.VerifiedRevision, "The slow test must not verify the edited configuration.");
        Assert.AreEqual(saved.ConfigRevision + 1, after.ConfigRevision);
    }

    [TestMethod]
    public async Task Tests_AreRateLimitedPerDestination()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        await h.Config.SaveSmtpAsync(Smtp(), (await DestinationAsync(h, NotificationProviderKinds.Smtp)).ConfigRevision, CancellationToken.None);
        var saved = await DestinationAsync(h, NotificationProviderKinds.Smtp);

        Assert.AreEqual(NotificationOperationStatus.Ok, (await h.Config.TestAsync(NotificationProviderKinds.Smtp, saved.ConfigRevision, CancellationToken.None)).Status);
        var second = await h.Config.TestAsync(NotificationProviderKinds.Smtp, saved.ConfigRevision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.RateLimited, second.Status);
        Assert.IsNotNull(second.RetryAfter);

        h.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.AreEqual(NotificationOperationStatus.Ok, (await h.Config.TestAsync(NotificationProviderKinds.Smtp, saved.ConfigRevision, CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Matrix_RequiresHttpsAndStoresTheTokenEncrypted_AndEditingResetsDeviceIdentity()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        var rev = (await DestinationAsync(h, NotificationProviderKinds.Matrix)).ConfigRevision;

        var plain = await h.Config.SaveMatrixAsync(new("http://hs.lab:8008", "!room:hs.lab", "tok", false, AllowInsecureHttp: true), rev, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Invalid, plain.Status, "Plain HTTP needs the isolated-lab gate.");

        var ok = await h.Config.SaveMatrixAsync(new("https://matrix.example.org/", "!room:example.org", "syt_secret", false, false), rev, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, ok.Status, ok.Message);
        var saved = await DestinationAsync(h, NotificationProviderKinds.Matrix);
        Assert.AreEqual("https://matrix.example.org", saved.Matrix!.HomeserverUrl, "A trailing slash is normalised away.");
        Assert.AreEqual("syt_secret", h.Encryption.Decrypt(saved.Matrix.AccessTokenEncrypted!));

        await using (var db = h.NewContext())
        {
            var row = await db.NotificationMatrixSettings.SingleAsync();
            row.DeviceId = "DEVICE1";
            row.BotUserId = "@bot:example.org";
            await db.SaveChangesAsync();
        }

        await h.Config.SaveMatrixAsync(new("https://matrix.example.org", "!other:example.org", null, false, false), saved.ConfigRevision, CancellationToken.None);
        var changedRoom = await DestinationAsync(h, NotificationProviderKinds.Matrix);
        Assert.IsNull(changedRoom.Matrix!.DeviceId, "A new room invalidates the resolved device identity.");
        Assert.AreEqual(saved.DeliveryIdentityRevision + 1, changedRoom.DeliveryIdentityRevision);
    }

    [TestMethod]
    public async Task Routes_RequireAKnownAvailableKey_AndAVerifiedEnabledDestination()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        var route = async (string key) => (await h.NewContext().NotificationRoutes.AsNoTracking().SingleAsync(r => r.NotificationKey == key));
        var epg = await route(NotificationKeys.EpgFetchFailed);

        Assert.AreEqual(NotificationOperationStatus.NotFound,
            (await h.Config.UpdateRouteAsync("matrix.message", new("smtp", true, true, null, null), 1, CancellationToken.None)).Status);
        Assert.AreEqual(NotificationOperationStatus.Conflict,
            (await h.Config.UpdateRouteAsync(NotificationKeys.EpgFetchFailed, new("smtp", true, true, null, null), epg.Revision, CancellationToken.None)).Status,
            "A destination that is not enabled and verified is not selectable.");

        await h.EnableAsync(NotificationProviderKinds.Smtp);
        var selected = await h.Config.UpdateRouteAsync(NotificationKeys.EpgFetchFailed, new("smtp", true, false, 5, 12), epg.Revision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, selected.Status, selected.Message);
        var updated = await route(NotificationKeys.EpgFetchFailed);
        Assert.IsNotNull(updated.DestinationId);
        Assert.IsFalse(updated.SendReminders);
        Assert.AreEqual(5, updated.FailureDelayMinutes);

        Assert.AreEqual(NotificationOperationStatus.Conflict,
            (await h.Config.UpdateRouteAsync(NotificationKeys.EpgFetchFailed, new(null, true, true, null, null), epg.Revision, CancellationToken.None)).Status, "Stale route revision.");
        Assert.AreEqual(NotificationOperationStatus.Invalid,
            (await h.Config.UpdateRouteAsync(NotificationKeys.EpgFetchFailed, new("smtp", true, true, 5000, null), updated.Revision, CancellationToken.None)).Status);

        var off = await h.Config.UpdateRouteAsync(NotificationKeys.EpgFetchFailed, new(null, true, true, null, null), updated.Revision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, off.Status);
        Assert.IsNull((await route(NotificationKeys.EpgFetchFailed)).DestinationId, "Off is a valid selection.");
    }

    [TestMethod]
    public async Task GlobalSettings_ValidateCoupledFieldsAtomically_AndResumeClearsRestoreActivation()
    {
        var (h, env) = await NewAsync();
        using var _e = env; await using var _h = h;
        await using (var db = h.NewContext())
        {
            (await db.NotificationSettings.SingleAsync()).RequiresActivation = true;
            await db.SaveChangesAsync();
        }

        var current = await h.NewContext().NotificationSettings.AsNoTracking().SingleAsync();
        NotificationSettingsUpdate Update(bool enabled = true, bool paused = false, int warnHours = 12, int recoverHours = 14) => new(
            enabled, paused, 10, 15, 6, warnHours, 90, recoverHours, 95, 30, 30);

        var bad = await h.Config.UpdateSettingsAsync(Update(warnHours: 24, recoverHours: 12), current.Revision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Invalid, bad.Status);
        Assert.IsTrue(bad.Errors!.ContainsKey("coverageRecoverHours"));
        Assert.AreEqual(current.Revision, (await h.NewContext().NotificationSettings.AsNoTracking().SingleAsync()).Revision, "A rejected update changes nothing.");

        Assert.AreEqual(NotificationOperationStatus.Conflict, (await h.Config.UpdateSettingsAsync(Update(), current.Revision + 7, CancellationToken.None)).Status);

        var paused = await h.Config.UpdateSettingsAsync(Update(paused: true), current.Revision, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, paused.Status);
        Assert.IsTrue((await h.NewContext().NotificationSettings.AsNoTracking().SingleAsync()).RequiresActivation, "Pausing does not acknowledge a restore.");

        var resumed = await h.Config.UpdateSettingsAsync(Update(), paused.Revision!.Value, CancellationToken.None);
        Assert.AreEqual(NotificationOperationStatus.Ok, resumed.Status);
        var after = await h.NewContext().NotificationSettings.AsNoTracking().SingleAsync();
        Assert.IsFalse(after.RequiresActivation);
        Assert.IsTrue(NotificationRouting.IsSendingAllowed(after));
        Assert.IsGreaterThan(current.ActivationEpoch, after.ActivationEpoch, "Resuming starts a new activation epoch.");
    }

    private sealed class EnvScope : IDisposable
    {
        private readonly string? _previousKey = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY");
        private readonly string? _previousKeys = Environment.GetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS");

        public EnvScope(string key)
        {
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", key);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", null);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEY", _previousKey);
            Environment.SetEnvironmentVariable("M3UNDLE_ENCRYPTION_KEYS", _previousKeys);
        }
    }
}
