using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application.Notifications;

/// <summary>A destination whose saved configuration is complete enough to send to, with decrypted secrets in memory only.</summary>
public sealed record ResolvedNotificationDestination(
    string DestinationId,
    string Kind,
    bool Enabled,
    int ConfigRevision,
    int DeliveryIdentityRevision,
    NotificationProviderConfiguration Configuration,
    IReadOnlyList<NotificationTarget> Targets);

/// <summary>
/// Owns notification configuration. Every operation opens its own scope, so a Blazor circuit never keeps a
/// DbContext alive across concurrent page actions. Save never sends or enables; verification binds to the exact saved
/// revision; secrets are write-only and never leave this class.
/// </summary>
public sealed class NotificationConfigurationService(
    IServiceScopeFactory scopeFactory,
    NotificationDestinationAdapters adapters,
    NotificationProviderRegistry providers,
    SecretEncryptionService encryption,
    NotificationRuntimeOptions options,
    NotificationActionThrottle throttle,
    NotificationSignal reconcileSignal,
    TimeProvider timeProvider)
{
    public const string MatrixRoomTargetId = Providers.MatrixDestinationAdapter.RoomTargetId;

    private static readonly NotificationOperationResult NotFound = new(NotificationOperationStatus.NotFound);

    /// <summary>Idempotent: creates the disabled global settings, both destinations and an Off route for every catalog key.</summary>
    public async Task EnsureSeededAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await EnsureSeededAsync(db, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
    }

    public static async Task EnsureSeededAsync(ApplicationDbContext db, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!await db.NotificationSettings.AnyAsync(cancellationToken))
            db.NotificationSettings.Add(new NotificationSettings { UpdatedUtc = nowUtc, IdentifierSalt = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) });

        var kinds = await db.NotificationDestinations.Select(x => x.Kind).ToListAsync(cancellationToken);
        foreach (var kind in new[] { NotificationProviderKinds.Matrix, NotificationProviderKinds.Smtp })
        {
            if (kinds.Contains(kind))
                continue;

            var destination = new NotificationDestination
            {
                DestinationId = Guid.NewGuid().ToString(),
                Kind = kind,
                Enabled = false,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            };
            if (kind == NotificationProviderKinds.Matrix)
                destination.Matrix = new NotificationMatrixSettings { DestinationId = destination.DestinationId };
            else
                destination.Smtp = new NotificationSmtpSettings { DestinationId = destination.DestinationId };
            db.NotificationDestinations.Add(destination);
        }

        var routeKeys = await db.NotificationRoutes.Select(x => x.NotificationKey).ToListAsync(cancellationToken);
        foreach (var definition in NotificationCatalog.Definitions.Where(d => !routeKeys.Contains(d.Key)))
        {
            db.NotificationRoutes.Add(new NotificationRoute
            {
                NotificationKey = definition.Key,
                DestinationId = null,
                UpdatedUtc = nowUtc,
            });
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Returns null when the destination is missing or its saved setup is incomplete or unusable.</summary>
    public async Task<ResolvedNotificationDestination?> ResolveAsync(string kind, CancellationToken cancellationToken)
    {
        var adapter = adapters.Find(kind);
        if (adapter is null)
            return null;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var destination = await db.NotificationDestinations.AsNoTracking()
            .Include(x => x.Matrix)
            .Include(x => x.Smtp)
            .Include(x => x.Recipients)
            .FirstOrDefaultAsync(x => x.Kind == kind, cancellationToken);
        if (destination is null)
            return null;

        var configuration = adapter.ResolveConfiguration(destination);
        if (configuration is null)
            return null;

        return new ResolvedNotificationDestination(
            destination.DestinationId, destination.Kind, destination.Enabled,
            destination.ConfigRevision, destination.DeliveryIdentityRevision, configuration, adapter.GetTargets(destination));
    }

    // ------------------------------------------------------------------------------------------------ setup

    public async Task<NotificationOperationResult> SaveSmtpAsync(SmtpSetupRequest request, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var destination = await db.NotificationDestinations
            .Include(x => x.Smtp).Include(x => x.Recipients)
            .FirstOrDefaultAsync(x => x.Kind == NotificationProviderKinds.Smtp, cancellationToken);
        if (destination is null)
            return NotFound;
        if (destination.ConfigRevision != expectedRevision)
            return Stale();

        var smtp = destination.Smtp ??= new NotificationSmtpSettings { DestinationId = destination.DestinationId };

        // Passwords are preserved exactly: not trimmed, not normalised. Blank means keep; clear is its own flag.
        string? newPasswordCipher = smtp.PasswordEncrypted;
        string? newPlain = null;
        if (request.ClearPassword)
            newPasswordCipher = null;
        else if (!string.IsNullOrEmpty(request.Password))
        {
            if (!encryption.IsAvailable)
                return Invalid("password", "No encryption key is configured, so a password cannot be stored securely.");
            newPlain = request.Password;
            newPasswordCipher = encryption.Encrypt(request.Password);
        }

        var candidate = new NotificationSmtpSettings
        {
            Host = request.Host?.Trim(),
            Port = request.Port,
            TlsMode = request.TlsMode,
            AuthMode = request.AuthMode,
            Username = request.Username,
            SenderAddress = request.SenderAddress?.Trim(),
            SenderName = string.IsNullOrWhiteSpace(request.SenderName) ? null : request.SenderName.Trim(),
        };

        var errors = NotificationValidation.ValidateSmtp(candidate, hasPassword: newPasswordCipher is not null);
        var recipientErrors = NotificationValidation.ValidateRecipients(request.Recipients, out var recipients);
        var merged = Merge(errors, recipientErrors);
        if (merged is not null)
            return new(NotificationOperationStatus.Invalid, "Fix the highlighted fields.", merged);

        var credentialChanged = request.ClearPassword
            ? smtp.PasswordEncrypted is not null
            : newPlain is not null && !SamePlaintext(smtp.PasswordEncrypted, newPlain);
        var identityChanged = credentialChanged
            || !string.Equals(smtp.Host, candidate.Host, StringComparison.Ordinal)
            || smtp.Port != candidate.Port
            || smtp.TlsMode != candidate.TlsMode
            || smtp.AuthMode != candidate.AuthMode
            || !string.Equals(smtp.Username, candidate.Username, StringComparison.Ordinal)
            || !string.Equals(smtp.SenderAddress, candidate.SenderAddress, StringComparison.Ordinal);

        var existingByKey = destination.Recipients.ToDictionary(r => r.CanonicalKey, StringComparer.Ordinal);
        var desiredKeys = recipients.Select(r => r.CanonicalKey).ToHashSet(StringComparer.Ordinal);
        var recipientsChanged = existingByKey.Count != desiredKeys.Count
            || existingByKey.Keys.Any(k => !desiredKeys.Contains(k))
            || recipients.Select((r, i) => (r, i)).Any(x => existingByKey.TryGetValue(x.r.CanonicalKey, out var row)
                && (row.SortOrder != x.i || !string.Equals(row.Address, x.r.Address, StringComparison.Ordinal)));
        var changed = identityChanged || recipientsChanged || !string.Equals(smtp.SenderName, candidate.SenderName, StringComparison.Ordinal)
            || (request.ClearPassword && smtp.PasswordEncrypted is not null);

        if (!changed)
            return new(NotificationOperationStatus.Ok, "No changes.", Revision: destination.ConfigRevision);

        smtp.Host = candidate.Host;
        smtp.Port = candidate.Port;
        smtp.TlsMode = candidate.TlsMode;
        smtp.AuthMode = candidate.AuthMode;
        smtp.Username = candidate.AuthMode == "password" ? candidate.Username : null;
        smtp.PasswordEncrypted = candidate.AuthMode == "password" ? newPasswordCipher : null;
        smtp.SenderAddress = candidate.SenderAddress;
        smtp.SenderName = candidate.SenderName;

        // Recipients keep their IDs when they stay, so adding one never re-targets history or re-sends to the others.
        foreach (var (key, row) in existingByKey.Where(x => !desiredKeys.Contains(x.Key)).ToList())
            db.NotificationEmailRecipients.Remove(row);
        for (var i = 0; i < recipients.Count; i++)
        {
            var (address, key) = recipients[i];
            if (existingByKey.TryGetValue(key, out var row))
            {
                row.Address = address;
                row.SortOrder = i;
            }
            else
            {
                db.NotificationEmailRecipients.Add(new NotificationEmailRecipient
                {
                    RecipientId = Guid.NewGuid().ToString(),
                    DestinationId = destination.DestinationId,
                    Address = address,
                    CanonicalKey = key,
                    SortOrder = i,
                });
            }
        }

        return await CommitChangeAsync(db, destination, identityChanged, cancellationToken);
    }

    public async Task<NotificationOperationResult> SaveMatrixAsync(MatrixSetupRequest request, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var destination = await db.NotificationDestinations
            .Include(x => x.Matrix)
            .FirstOrDefaultAsync(x => x.Kind == NotificationProviderKinds.Matrix, cancellationToken);
        if (destination is null)
            return NotFound;
        if (destination.ConfigRevision != expectedRevision)
            return Stale();

        var matrix = destination.Matrix ??= new NotificationMatrixSettings { DestinationId = destination.DestinationId };

        string? newTokenCipher = matrix.AccessTokenEncrypted;
        string? newPlain = null;
        if (request.ClearAccessToken)
            newTokenCipher = null;
        else if (!string.IsNullOrEmpty(request.AccessToken))
        {
            if (!encryption.IsAvailable)
                return Invalid("accessToken", "No encryption key is configured, so a token cannot be stored securely.");
            newPlain = request.AccessToken;
            newTokenCipher = encryption.Encrypt(request.AccessToken);
        }

        var candidate = new NotificationMatrixSettings
        {
            HomeserverUrl = request.HomeserverUrl?.Trim().TrimEnd('/'),
            RoomId = request.RoomId?.Trim(),
            AllowInsecureHttp = request.AllowInsecureHttp,
        };

        var errors = NotificationValidation.ValidateMatrix(candidate, newTokenCipher is not null, options.AllowInsecureMatrixHttp);
        if (!errors.IsValid)
            return new(NotificationOperationStatus.Invalid, "Fix the highlighted fields.", errors.ToDictionary());

        var credentialChanged = request.ClearAccessToken
            ? matrix.AccessTokenEncrypted is not null
            : newPlain is not null && !SamePlaintext(matrix.AccessTokenEncrypted, newPlain);
        var identityChanged = credentialChanged
            || !string.Equals(matrix.HomeserverUrl, candidate.HomeserverUrl, StringComparison.Ordinal)
            || !string.Equals(matrix.RoomId, candidate.RoomId, StringComparison.Ordinal);
        var changed = identityChanged || matrix.AllowInsecureHttp != candidate.AllowInsecureHttp;

        if (!changed)
            return new(NotificationOperationStatus.Ok, "No changes.", Revision: destination.ConfigRevision);

        matrix.HomeserverUrl = candidate.HomeserverUrl;
        matrix.RoomId = candidate.RoomId;
        matrix.AllowInsecureHttp = candidate.AllowInsecureHttp;
        matrix.AccessTokenEncrypted = newTokenCipher;
        if (identityChanged)
        {
            // The device behind a different homeserver, room or token is a different identity; the next test resolves it.
            matrix.BotUserId = null;
            matrix.DeviceId = null;
        }

        return await CommitChangeAsync(db, destination, identityChanged, cancellationToken);
    }

    /// <summary>
    /// Any saved change invalidates the test of the previous revision and switches the destination off, so the sequence is
    /// always: save, test that exact revision, then enable. Unclaimed work bound to the old revision is suppressed by the
    /// next reconciliation; nothing is ever re-addressed.
    /// </summary>
    private async Task<NotificationOperationResult> CommitChangeAsync(
        ApplicationDbContext db, NotificationDestination destination, bool identityChanged, CancellationToken cancellationToken)
    {
        destination.ConfigRevision++;
        if (identityChanged)
            destination.DeliveryIdentityRevision++;
        destination.Enabled = false;
        destination.VerifiedRevision = null;
        destination.VerifiedUtc = null;
        destination.VerificationStatus = NotificationVerificationStates.Unverified;
        destination.VerificationDetail = null;
        destination.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale();
        }

        reconcileSignal.Wake();
        return new(NotificationOperationStatus.Ok, "Saved. Test this configuration before enabling it.", Revision: destination.ConfigRevision);
    }

    public async Task<NotificationOperationResult> SetDestinationEnabledAsync(
        string kind, bool enabled, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var destination = await db.NotificationDestinations
            .Include(x => x.Matrix).Include(x => x.Smtp).Include(x => x.Recipients)
            .FirstOrDefaultAsync(x => x.Kind == kind, cancellationToken);
        if (destination is null)
            return NotFound;
        if (destination.ConfigRevision != expectedRevision)
            return Stale();

        if (enabled)
        {
            var adapter = adapters.Find(kind);
            if (adapter is null || !adapter.IsStructurallyComplete(destination))
                return new(NotificationOperationStatus.Invalid, "Finish the configuration before enabling this destination.");
            if (destination.VerifiedRevision != destination.ConfigRevision)
                return new(NotificationOperationStatus.Conflict, "Test the saved configuration successfully before enabling it.");
        }

        if (destination.Enabled == enabled)
            return new(NotificationOperationStatus.Ok, Revision: destination.ConfigRevision);

        destination.Enabled = enabled;
        destination.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        if (enabled)
            await BumpEpochAsync(db, cancellationToken);
        reconcileSignal.Wake();
        return new(NotificationOperationStatus.Ok, Revision: destination.ConfigRevision);
    }

    // ------------------------------------------------------------------------------------------------ global settings and routes

    public async Task<NotificationOperationResult> UpdateSettingsAsync(
        NotificationSettingsUpdate update, int expectedRevision, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var settings = await db.NotificationSettings.SingleAsync(cancellationToken);
        if (settings.Revision != expectedRevision)
            return Stale();

        var wasAllowed = NotificationRouting.IsSendingAllowed(settings);
        var candidate = new NotificationSettings
        {
            FailureDelayMinutes = update.FailureDelayMinutes,
            OverdueGraceMinutes = update.OverdueGraceMinutes,
            ReminderIntervalHours = update.ReminderIntervalHours,
            CoverageWarnHours = update.CoverageWarnHours,
            CoverageWarnPercent = update.CoverageWarnPercent,
            CoverageRecoverHours = update.CoverageRecoverHours,
            CoverageRecoverPercent = update.CoverageRecoverPercent,
            CoverageGapMinutes = update.CoverageGapMinutes,
            RetentionDays = update.RetentionDays,
        };
        var errors = NotificationValidation.ValidatePolicy(candidate);
        if (!errors.IsValid)
            return new(NotificationOperationStatus.Invalid, "Fix the highlighted fields.", errors.ToDictionary());

        settings.FailureDelayMinutes = candidate.FailureDelayMinutes;
        settings.OverdueGraceMinutes = candidate.OverdueGraceMinutes;
        settings.ReminderIntervalHours = candidate.ReminderIntervalHours;
        settings.CoverageWarnHours = candidate.CoverageWarnHours;
        settings.CoverageWarnPercent = candidate.CoverageWarnPercent;
        settings.CoverageRecoverHours = candidate.CoverageRecoverHours;
        settings.CoverageRecoverPercent = candidate.CoverageRecoverPercent;
        settings.CoverageGapMinutes = candidate.CoverageGapMinutes;
        settings.RetentionDays = candidate.RetentionDays;
        settings.SendingEnabled = update.SendingEnabled;
        settings.Paused = update.Paused;

        // Turning sending on, or resuming, is the administrator's explicit acknowledgement after a restore.
        if (update.SendingEnabled && !update.Paused)
            settings.RequiresActivation = false;

        settings.Revision++;
        settings.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale();
        }

        if (!wasAllowed && NotificationRouting.IsSendingAllowed(settings))
            await BumpEpochAsync(db, cancellationToken);
        reconcileSignal.Wake();
        return new(NotificationOperationStatus.Ok, Revision: settings.Revision);
    }

    public async Task<NotificationOperationResult> UpdateRouteAsync(
        string notificationKey, NotificationRouteUpdate update, int expectedRevision, CancellationToken cancellationToken)
    {
        var definition = NotificationCatalog.Find(notificationKey);
        if (definition is null)
            return NotFound;
        if (!definition.ProducerAvailable)
            return new(NotificationOperationStatus.Invalid, "This notification is not available yet.");

        var errors = new NotificationValidationErrors();
        if (update.FailureDelayMinutes is < 0 or > 1440)
            errors.Add("failureDelayMinutes", "Must be between 0 and 1,440.");
        if (update.ReminderIntervalHours is < 1 or > 168)
            errors.Add("reminderIntervalHours", "Must be between 1 and 168.");
        if (!errors.IsValid)
            return new(NotificationOperationStatus.Invalid, "Fix the highlighted fields.", errors.ToDictionary());

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var route = await db.NotificationRoutes.FirstOrDefaultAsync(x => x.NotificationKey == notificationKey, cancellationToken);
        if (route is null)
            return NotFound;
        if (route.Revision != expectedRevision)
            return Stale();

        string? destinationId = null;
        if (update.DestinationKind is not null)
        {
            var destination = await db.NotificationDestinations.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Kind == update.DestinationKind, cancellationToken);
            if (destination is null)
                return new(NotificationOperationStatus.Invalid, "Unknown notification method.");
            if (!destination.Enabled || destination.VerifiedRevision != destination.ConfigRevision)
                return new(NotificationOperationStatus.Conflict, "Enable and successfully test this method before selecting it.");
            destinationId = destination.DestinationId;
        }

        var destinationChanged = route.DestinationId != destinationId;
        route.DestinationId = destinationId;
        route.SendRecovery = definition.SupportsRecovery ? update.SendRecovery : true;
        route.SendReminders = definition.SupportsReminders ? update.SendReminders : true;
        route.FailureDelayMinutes = definition.Lifecycle == NotificationLifecycle.Incident ? update.FailureDelayMinutes : null;
        route.ReminderIntervalHours = definition.SupportsReminders ? update.ReminderIntervalHours : null;
        route.Revision++;
        route.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale();
        }

        if (destinationChanged && destinationId is not null)
            await BumpEpochAsync(db, cancellationToken);
        reconcileSignal.Wake();
        return new(NotificationOperationStatus.Ok, Revision: route.Revision);
    }

    // ------------------------------------------------------------------------------------------------ verification

    /// <summary>
    /// Sends a fixed test message to every configured target through the real provider and records the outcome against the
    /// exact revision tested. An edit made while the test ran leaves that newer revision unverified.
    /// </summary>
    public async Task<NotificationTestResult> TestAsync(string kind, int expectedRevision, CancellationToken cancellationToken)
    {
        if (!throttle.TryAcquire($"test:{kind}", limit: 1, TimeSpan.FromMinutes(1), serverWideLimit: 10, out var retryAfter))
            return Refused(NotificationOperationStatus.RateLimited, "Wait a minute before testing again.", retryAfter);

        var adapter = adapters.Find(kind);
        var provider = providers.Find(kind);
        if (adapter is null || provider is null)
            return Refused(NotificationOperationStatus.NotFound, "This notification method is not available.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var destination = await NotificationRouting.LoadDestinationAsync(db, await DestinationIdAsync(db, kind, cancellationToken) ?? string.Empty, cancellationToken);
        if (destination is null)
            return Refused(NotificationOperationStatus.NotFound, "This notification method is not configured.");
        if (destination.ConfigRevision != expectedRevision)
            return Refused(NotificationOperationStatus.Conflict, "The configuration changed. Reload and test the saved version.");
        if (!adapter.IsStructurallyComplete(destination))
            return Refused(NotificationOperationStatus.Invalid, "Finish the configuration before testing it.");

        var configuration = adapter.ResolveConfiguration(destination);
        if (configuration is null)
            return Refused(NotificationOperationStatus.Invalid, "A stored secret could not be read. Re-enter it and save.");

        var tested = destination.ConfigRevision;
        var results = new List<NotificationTargetTestResult>();

        // A provider that can check its destination first does so before any message is sent, and whatever it discovers
        // (for example the Matrix device) is stored. A discovered identity change invalidates this test.
        if (provider is INotificationConnectionValidator validator)
        {
            NotificationConnectionCheck check;
            using var checkDeadline = new CancellationTokenSource(NotificationRetryPolicy.NetworkDeadline, timeProvider);
            using var checkLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, checkDeadline.Token);
            try
            {
                check = await validator.CheckAsync(configuration, checkLinked.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                check = new NotificationConnectionCheck(false, "timeout", "The connection check did not finish before the deadline.");
            }
            catch (Exception)
            {
                check = new NotificationConnectionCheck(false, "provider_error", "The connection check failed unexpectedly.");
            }

            if (!check.Succeeded)
            {
                var failure = check.ErrorText ?? "The connection check failed.";
                var recorded = await RecordVerificationAsync(db, destination.DestinationId, tested, NotificationVerificationStates.Failed, failure, verified: false);
                return new NotificationTestResult(
                    NotificationOperationStatus.Ok,
                    recorded ? NotificationVerificationStates.Failed : NotificationVerificationStates.Unverified,
                    failure,
                    [new NotificationTargetTestResult("Connection check", "Failed", check.ErrorCode)],
                    tested, false);
            }

            if (check.Discovered is { Count: > 0 })
            {
                var tracked = await db.NotificationDestinations
                    .Include(d => d.Matrix).Include(d => d.Smtp).Include(d => d.Recipients)
                    .FirstAsync(d => d.DestinationId == destination.DestinationId, cancellationToken);
                var identityBefore = tracked.DeliveryIdentityRevision;
                if (adapter.ApplyDiscovered(tracked, check.Discovered))
                {
                    if (tracked.DeliveryIdentityRevision != identityBefore)
                    {
                        tracked.ConfigRevision++;
                        tracked.Enabled = false;
                        tracked.VerifiedRevision = null;
                        tracked.VerifiedUtc = null;
                        tracked.VerificationStatus = NotificationVerificationStates.Unverified;
                    }

                    tracked.UpdatedUtc = timeProvider.GetUtcNow().UtcDateTime;
                    try
                    {
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        return Refused(NotificationOperationStatus.Conflict, "The configuration changed during the test. Reload and test again.");
                    }

                    if (tracked.ConfigRevision != tested)
                        return Refused(NotificationOperationStatus.Conflict, "The destination's identity changed, so the previous setup is no longer verified. Test again.");

                    var refreshed = adapter.ResolveConfiguration(tracked);
                    if (refreshed is null)
                        return Refused(NotificationOperationStatus.Invalid, "A stored secret could not be read. Re-enter it and save.");
                    configuration = refreshed;
                }
            }
        }

        foreach (var target in adapter.GetTargets(destination))
        {
            var id = Guid.NewGuid().ToString();
            var request = new NotificationSendRequest(
                $"test-{id}", $"<test-{id}@m3undle.local>",
                new NotificationMessage(
                    "M3Undle test notification",
                    "This is a test message from M3Undle. It confirms the transport accepted a message from this configuration. " +
                    "Acceptance does not prove a person saw it.",
                    "Info", null, timeProvider.GetUtcNow().UtcDateTime),
                target, tested, configuration);

            NotificationSendResult result;
            using var deadline = new CancellationTokenSource(NotificationRetryPolicy.NetworkDeadline, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                result = await provider.SendAsync(request, linked.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                result = NotificationSendResult.Uncertain("timeout", "The test did not finish before the deadline.");
            }
            catch (Exception)
            {
                result = NotificationSendResult.Uncertain("provider_error", "The provider failed unexpectedly during the test.");
            }

            results.Add(new NotificationTargetTestResult(
                target.Label,
                result.Outcome switch
                {
                    NotificationSendOutcome.Accepted => "Accepted",
                    NotificationSendOutcome.Uncertain => "Uncertain",
                    _ => "Failed",
                },
                result.Outcome == NotificationSendOutcome.Accepted ? null : result.ErrorCode));
        }

        var accepted = results.Count(r => r.Outcome == "Accepted");
        var uncertain = results.Any(r => r.Outcome == "Uncertain");
        var status = accepted == results.Count ? NotificationVerificationStates.Verified
            : uncertain ? NotificationVerificationStates.Uncertain
            : accepted > 0 ? NotificationVerificationStates.Partial
            : NotificationVerificationStates.Failed;
        var summary = accepted == results.Count
            ? $"All {results.Count} target(s) accepted the test message. Acceptance is not proof of receipt."
            : $"{accepted} of {results.Count} target(s) accepted the test message.";

        var verified = status == NotificationVerificationStates.Verified;
        var applied = await RecordVerificationAsync(db, destination.DestinationId, tested, status, summary, verified);

        if (applied && verified)
        {
            await BumpEpochAsync(db, CancellationToken.None);
            reconcileSignal.Wake();
        }

        return new NotificationTestResult(
            NotificationOperationStatus.Ok, applied ? status : NotificationVerificationStates.Unverified,
            applied ? summary : "The configuration changed while the test ran, so the result was not applied.",
            results, tested, applied && verified);
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>Binds a test outcome to the exact revision tested. Returns false when the configuration moved on meanwhile.</summary>
    private async Task<bool> RecordVerificationAsync(
        ApplicationDbContext db, string destinationId, int testedRevision, string status, string detail, bool verified)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.NotificationDestinations
            .Where(d => d.DestinationId == destinationId && d.ConfigRevision == testedRevision)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.VerificationStatus, status)
                .SetProperty(d => d.VerificationDetail, detail)
                .SetProperty(d => d.VerifiedRevision, verified ? testedRevision : (int?)null)
                .SetProperty(d => d.VerifiedUtc, verified ? now : (DateTime?)null), CancellationToken.None) == 1;
    }

    /// <summary>A new activation epoch gives refreshed current-state occurrences their own identity.</summary>
    private static Task<int> BumpEpochAsync(ApplicationDbContext db, CancellationToken cancellationToken) =>
        db.NotificationSettings.ExecuteUpdateAsync(u => u.SetProperty(s => s.ActivationEpoch, s => s.ActivationEpoch + 1), cancellationToken);

    private static async Task<string?> DestinationIdAsync(ApplicationDbContext db, string kind, CancellationToken cancellationToken) =>
        await db.NotificationDestinations.AsNoTracking().Where(d => d.Kind == kind).Select(d => d.DestinationId).FirstOrDefaultAsync(cancellationToken);

    private bool SamePlaintext(string? storedCipher, string plain)
    {
        if (storedCipher is null)
            return false;
        try
        {
            return string.Equals(encryption.Decrypt(storedCipher), plain, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static NotificationOperationResult Stale() =>
        new(NotificationOperationStatus.Conflict, "This configuration changed since you loaded it. Reload and try again.");

    private static NotificationOperationResult Invalid(string field, string message) =>
        new(NotificationOperationStatus.Invalid, message, new Dictionary<string, string[]> { [field] = [message] });

    private static IDictionary<string, string[]>? Merge(params NotificationValidationErrors[] sets)
    {
        var merged = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var set in sets.Where(s => !s.IsValid))
        {
            foreach (var (field, messages) in set.ToDictionary())
                merged[field] = merged.TryGetValue(field, out var existing) ? [.. existing, .. messages] : messages;
        }

        return merged.Count == 0 ? null : merged;
    }

    private static NotificationTestResult Refused(NotificationOperationStatus status, string message, TimeSpan? retryAfter = null) =>
        new(status, NotificationVerificationStates.Unverified, message, [], null, false, retryAfter);
}
