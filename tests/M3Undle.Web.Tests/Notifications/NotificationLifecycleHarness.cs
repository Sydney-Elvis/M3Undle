using M3Undle.Core.Epg;
using M3Undle.Web.Application;
using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Application.Notifications.Providers;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace M3Undle.Web.Tests.Notifications;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public DateTime UtcNow => _now.UtcDateTime;
    public void Advance(TimeSpan span) => _now += span;
}

internal sealed class FixedScheduleService(int? intervalHours) : IRefreshScheduleService
{
    public int? IntervalHours { get; set; } = intervalHours;

    public Task<RefreshScheduleSettings> GetSettingsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(bool Succeeded, string? Error)> UpdateAsync(RefreshScheduleSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<DateTime?> GetNextScheduledRefreshUtcAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<EffectiveRefreshScheduleSettings> GetEffectiveSettingsAsync(string profileId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(bool Succeeded, string? Error)> UpdateProfileAsync(string profileId, ProfileRefreshScheduleSettings settings, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<DateTime?> GetNextScheduledRefreshUtcAsync(string profileId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<EffectiveRefreshScheduleSettings?> GetActiveProfileSettingsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<EffectiveRefreshScheduleSettings?>(IntervalHours is { } h
            ? new("profile-1", new RefreshScheduleSettings($"{h}h", false), false, new RefreshScheduleSettings($"{h}h", false))
            : new EffectiveRefreshScheduleSettings("profile-1", new RefreshScheduleSettings("manual", false), false, new RefreshScheduleSettings("manual", false)));
    }
}

internal sealed class LabRuntimeOptions(EnvironmentVariableService env, bool allowHttp) : NotificationRuntimeOptions(env)
{
    public override bool AllowInsecureMatrixHttp => allowHttp;
}

/// <summary>Keeps the real completeness/target rules but resolves a fixed fake secret, so worker tests need no key material.</summary>
internal sealed class PassThroughAdapter(INotificationDestinationAdapter real) : INotificationDestinationAdapter
{
    public string Kind => real.Kind;
    public bool IsStructurallyComplete(NotificationDestination destination) => real.IsStructurallyComplete(destination);
    public IReadOnlyList<NotificationTarget> GetTargets(NotificationDestination destination) => real.GetTargets(destination);

    public NotificationProviderConfiguration? ResolveConfiguration(NotificationDestination destination) => Kind == NotificationProviderKinds.Matrix
        ? new MatrixProviderConfiguration("https://matrix.example.org", "!room:example.org", new NotificationSecret("token"), null, null, false)
        : new SmtpProviderConfiguration("smtp.example.org", 587, "starttls", "none", null, null, "m3undle@example.org", null);
}

internal sealed class FakeActiveStreams : IActiveStreamSource
{
    public List<ActiveChannelStream> Sessions { get; } = [];
    public IReadOnlyList<ActiveChannelStream> GetActive() => [.. Sessions];
}

/// <summary>A provider whose outcomes a test scripts. Defaults to Accepted.</summary>
internal sealed class ScriptedProvider(string kind, bool idempotent = false) : INotificationProvider
{
    private readonly Queue<Func<NotificationSendRequest, CancellationToken, Task<NotificationSendResult>>> _script = new();
    private int _calls;

    public string Kind => kind;
    public bool SupportsIdempotentRetry => idempotent;
    public int Calls => _calls;
    public List<NotificationSendRequest> Requests { get; } = [];

    public ScriptedProvider Then(NotificationSendResult result)
    {
        _script.Enqueue((_, _) => Task.FromResult(result));
        return this;
    }

    public ScriptedProvider Then(Func<NotificationSendRequest, CancellationToken, Task<NotificationSendResult>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public async Task<NotificationSendResult> SendAsync(NotificationSendRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Requests)
            Requests.Add(request);
        Func<NotificationSendRequest, CancellationToken, Task<NotificationSendResult>>? step = null;
        lock (_script)
        {
            if (_script.Count > 0)
                step = _script.Dequeue();
        }

        return step is null ? NotificationSendResult.Accepted($"ref-{request.DeliveryId}") : await step(request, cancellationToken);
    }
}

/// <summary>Runs the real reconciler and delivery processor, built from the production service graph, over a real migrated database under a manual clock.</summary>
internal sealed class NotificationLifecycleHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly List<string> _tempDirectories = [];

    public MigratedDatabase Database { get; }
    public ManualTimeProvider Time { get; }
    public NotificationRuntimeState State { get; } = new();
    public FixedScheduleService Schedule { get; }
    public NotificationProviderRegistry Registry { get; }
    public ScriptedProvider Matrix { get; } = new(NotificationProviderKinds.Matrix, idempotent: true);
    public ScriptedProvider Smtp { get; } = new(NotificationProviderKinds.Smtp);
    public FakeActiveStreams ActiveStreams { get; } = new();
    public int? DeliveryCeiling { get; set; }
    public double JitterValue { get; set; } = 0.5;

    private NotificationLifecycleHarness(
        MigratedDatabase database, ManualTimeProvider time, int? scheduleHours,
        IEnumerable<INotificationProvider>? extraProviders, IEnumerable<INotificationDestinationAdapter>? extraAdapters, bool realAdapters,
        IEnumerable<INotificationProvider>? providerOverrides, bool allowInsecureMatrixHttp)
    {
        Database = database;
        Time = time;
        Schedule = new FixedScheduleService(scheduleHours);
        Registry = new NotificationProviderRegistry([.. providerOverrides ?? [Matrix, Smtp], .. extraProviders ?? []]);

        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(envVars);
        services.AddSingleton(new EndpointUrlService(envVars));
        services.AddSingleton(State);
        services.AddSingleton<IRefreshScheduleService>(Schedule);
        services.AddSingleton(new XmltvParser());
        var dir = Path.Combine(Path.GetTempPath(), "m3undle-notif-tests");
        services.AddSingleton(new RuntimePaths(dir, Path.Combine(dir, "m3undle.db"), "Data Source=:memory:", dir, dir));
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(
            database.SharedConnection is { } connection ? connection : new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString!))
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        services.AddSingleton(encryption);
        services.AddSingleton<NotificationRuntimeOptions>(new LabRuntimeOptions(envVars, allowInsecureMatrixHttp));
        services.AddSingleton<NotificationActionThrottle>();
        if (realAdapters)
        {
            services.AddSingleton<INotificationDestinationAdapter>(new MatrixDestinationAdapter(encryption));
            services.AddSingleton<INotificationDestinationAdapter>(new SmtpDestinationAdapter(encryption));
        }
        else
        {
            services.AddSingleton<INotificationDestinationAdapter>(new PassThroughAdapter(new MatrixDestinationAdapter(encryption)));
            services.AddSingleton<INotificationDestinationAdapter>(new PassThroughAdapter(new SmtpDestinationAdapter(encryption)));
        }
        foreach (var extra in extraProviders ?? [])
            services.AddSingleton(extra);
        foreach (var adapter in extraAdapters ?? [])
            services.AddSingleton(adapter);
        services.AddSingleton<NotificationDestinationAdapters>();
        services.AddSingleton(Registry);
        services.AddSingleton<NotificationConfigurationService>();
        services.AddSingleton<NotificationSignal>();
        services.AddScoped<NotificationRouting>();
        services.AddScoped<NotificationOccurrenceWriter>();
        services.AddScoped<NotificationDeliveryService>(sp =>
        {
            var service = new NotificationDeliveryService(sp.GetRequiredService<EndpointUrlService>(), NullLogger<NotificationDeliveryService>.Instance);
            if (DeliveryCeiling is { } ceiling)
                service.Ceiling = ceiling;
            return service;
        });
        services.AddScoped<NotificationIncidentService>();
        services.AddScoped<EpgCoverageFacts>();
        services.AddScoped<EpgNotificationEvaluator>();
        services.AddScoped<OperationalNotificationEvaluator>();
        services.AddScoped<SecurityNotificationEvaluator>();
        services.AddScoped<NotificationSecurityRecorder>();
        services.AddSingleton<IActiveStreamSource>(ActiveStreams);
        services.AddScoped<NotificationRetention>();
        services.AddScoped<NotificationReconciler>();
        services.AddScoped<NotificationDeliveryActions>();
        services.AddScoped(sp => new NotificationDeliveryProcessor(
            sp.GetRequiredService<ApplicationDbContext>(), sp.GetRequiredService<NotificationProviderRegistry>(),
            sp.GetRequiredService<NotificationDestinationAdapters>(), sp.GetRequiredService<NotificationConfigurationService>(),
            sp.GetRequiredService<NotificationSignal>(), sp.GetRequiredService<TimeProvider>(),
            NullLogger<NotificationDeliveryProcessor>.Instance) { Jitter = () => JitterValue });
        _services = services.BuildServiceProvider();
    }

    public static async Task<NotificationLifecycleHarness> CreateAsync(
        int? scheduleHours = 6, bool fileBacked = false, IEnumerable<INotificationProvider>? extraProviders = null,
        IEnumerable<INotificationDestinationAdapter>? extraAdapters = null, bool realAdapters = false, string? databasePath = null,
        IEnumerable<INotificationProvider>? providerOverrides = null, bool allowInsecureMatrixHttp = false)
    {
        var database = databasePath is null
            ? await MigratedDatabase.CreateAsync(connectionPerContext: fileBacked)
            : await MigratedDatabase.CreateAsync(databasePath, connectionPerContext: true);
        var harness = new NotificationLifecycleHarness(
            database, new ManualTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)), scheduleHours, extraProviders, extraAdapters, realAdapters, providerOverrides, allowInsecureMatrixHttp);
        await using var db = database.CreateDbContext();
        await NotificationConfigurationService.EnsureSeededAsync(db, harness.Time.UtcNow, CancellationToken.None);
        return harness;
    }

    public ApplicationDbContext NewContext() => Database.CreateDbContext();

    public NotificationConfigurationService Config => _services.GetRequiredService<NotificationConfigurationService>();

    public SecretEncryptionService Encryption => _services.GetRequiredService<SecretEncryptionService>();

    public async Task RunAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<NotificationReconciler>().RunOnceAsync(CancellationToken.None);
    }

    /// <summary>Handles one due delivery for the kind, like one step of the worker. Returns whether it did anything.</summary>
    public async Task<bool> ProcessAsync(string kind, string owner = "worker-1", CancellationToken hostToken = default)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>().ProcessNextAsync(kind, owner, hostToken);
    }

    public async Task<int> RecoverExpiredAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationDeliveryProcessor>().RecoverExpiredClaimsAsync(CancellationToken.None);
    }

    public async Task<DeliveryActionResult> RetryAsync(string deliveryId, bool acknowledge, int? expectedRevision = null)
    {
        await using var scope = _services.CreateAsyncScope();
        var revision = expectedRevision ?? (await DeliveryAsync(deliveryId)).Revision;
        return await scope.ServiceProvider.GetRequiredService<NotificationDeliveryActions>().RetryAsync(deliveryId, revision, acknowledge, CancellationToken.None);
    }

    public async Task<DeliveryActionResult> DismissAsync(string deliveryId)
    {
        await using var scope = _services.CreateAsyncScope();
        var revision = (await DeliveryAsync(deliveryId)).Revision;
        return await scope.ServiceProvider.GetRequiredService<NotificationDeliveryActions>().DismissAsync(deliveryId, revision, CancellationToken.None);
    }

    public async Task<NotificationDelivery> DeliveryAsync(string deliveryId)
    {
        await using var db = NewContext();
        return await db.NotificationDeliveries.AsNoTracking().SingleAsync(d => d.DeliveryId == deliveryId);
    }

    public async Task SeedSourceAsync(string id = "src-1", string name = "Guide", string? providerId = "provider-1", Action<EpgSource>? configure = null)
    {
        await using var db = NewContext();
        if (providerId is not null && !await db.Providers.AnyAsync(p => p.ProviderId == providerId))
        {
            db.Providers.Add(new Provider
            {
                ProviderId = providerId, Name = "Provider", Enabled = true, PlaylistUrl = "http://x/p.m3u",
                CreatedUtc = Time.UtcNow, UpdatedUtc = Time.UtcNow,
            });
            if (!await db.Profiles.AnyAsync(p => p.ProfileId == "profile-1"))
            {
                db.Profiles.Add(new Profile
                {
                    ProfileId = "profile-1", Name = "Main", Enabled = true, IsActive = true, OutputName = "m3undle",
                    MergeMode = "single", CreatedUtc = Time.UtcNow, UpdatedUtc = Time.UtcNow,
                });
            }
            db.ProfileProviders.Add(new ProfileProvider { ProviderId = providerId, ProfileId = "profile-1", Priority = 1, Enabled = true });
        }

        var source = new EpgSource
        {
            EpgSourceId = id, ProviderId = providerId, Name = name, Kind = "xmltv_url", UrlOrPath = "http://x/g.xml",
            Enabled = true, CreatedUtc = Time.UtcNow, UpdatedUtc = Time.UtcNow,
        };
        configure?.Invoke(source);
        db.EpgSources.Add(source);
        await db.SaveChangesAsync();
    }

    /// <summary>Enables sending, enables and verifies the destination, and routes the given notification keys to it.</summary>
    public async Task EnableAsync(string kind, params string[] routedKeys)
    {
        await using var db = NewContext();
        var settings = await db.NotificationSettings.SingleAsync();
        settings.SendingEnabled = true;
        settings.ActivationEpoch++;
        settings.Revision++;

        var destination = await db.NotificationDestinations
            .Include(x => x.Matrix).Include(x => x.Smtp).Include(x => x.Recipients)
            .SingleAsync(x => x.Kind == kind);
        if (kind == NotificationProviderKinds.Matrix)
        {
            destination.Matrix!.HomeserverUrl = "https://matrix.example.org";
            destination.Matrix.RoomId = "!room:example.org";
            destination.Matrix.AccessTokenEncrypted = "encrypted";
        }
        else if (kind == NotificationProviderKinds.Smtp)
        {
            destination.Smtp!.Host = "smtp.example.org";
            destination.Smtp.AuthMode = "none";
            destination.Smtp.SenderAddress = "m3undle@example.org";
            if (destination.Recipients.Count == 0)
            {
                foreach (var (address, i) in new[] { "a@example.org", "b@example.org" }.Select((a, i) => (a, i)))
                {
                    destination.Recipients.Add(new NotificationEmailRecipient
                    {
                        RecipientId = $"rcpt-{i}", DestinationId = destination.DestinationId, Address = address,
                        CanonicalKey = address, SortOrder = i,
                    });
                }
            }
        }

        destination.Enabled = true;
        destination.VerifiedRevision = destination.ConfigRevision;
        destination.VerificationStatus = NotificationVerificationStates.Verified;
        foreach (var key in routedKeys)
        {
            var route = await db.NotificationRoutes.SingleAsync(r => r.NotificationKey == key);
            route.DestinationId = destination.DestinationId;
            route.Revision++;
        }

        await db.SaveChangesAsync();
    }

    public async Task StageObservationAsync(string sourceId, string outcome, string? detail = null)
    {
        await using var db = NewContext();
        new NotificationOccurrenceWriter(db, Time).StageObservation(
            NotificationEvidenceKeys.EpgSourceCheck, NotificationSubjectKinds.EpgSource, sourceId, outcome, Time.UtcNow, detail);

        // The real recorder commits the source columns in the same transaction as the observation.
        var source = await db.EpgSources.SingleAsync(x => x.EpgSourceId == sourceId);
        source.LastCheckedUtc = Time.UtcNow;
        source.LastCheckStatus = outcome == NotificationObservationOutcomes.Failed ? "fail" : outcome == NotificationObservationOutcomes.Ok ? "ok" : "not_modified";
        await db.SaveChangesAsync();
    }

    public async Task StageRawObservationAsync(string evidenceKey, string subjectKind, string subjectId, string outcome, string? detail = null)
    {
        await using var db = NewContext();
        new NotificationOccurrenceWriter(db, Time).StageObservation(evidenceKey, subjectKind, subjectId, outcome, Time.UtcNow, detail);
        await db.SaveChangesAsync();
    }

    public async Task<List<NotificationIncident>> IncidentsAsync(string? key = null)
    {
        await using var db = NewContext();
        return await db.NotificationIncidents.AsNoTracking()
            .Where(x => key == null || x.NotificationKey == key)
            .OrderBy(x => x.Generation).ToListAsync();
    }

    public async Task<List<(NotificationOccurrence Occurrence, NotificationDelivery Delivery)>> DeliveriesAsync()
    {
        await using var db = NewContext();
        var rows = await (from d in db.NotificationDeliveries.AsNoTracking()
                          join o in db.NotificationOccurrences.AsNoTracking() on d.OccurrenceId equals o.OccurrenceId
                          select new { d, o }).ToListAsync();
        return rows.OrderBy(x => x.d.CreatedUtc).ThenBy(x => x.o.Kind).Select(x => (x.o, x.d)).ToList();
    }

    /// <summary>Stand-in for the delivery worker recording an accepted send, including the per-target acceptance record.</summary>
    public async Task AcceptAsync(string deliveryId)
    {
        await using var db = NewContext();
        var delivery = await db.NotificationDeliveries.Include(d => d.Occurrence).SingleAsync(d => d.DeliveryId == deliveryId);
        delivery.State = NotificationDeliveryStates.Accepted;
        delivery.AcceptedUtc = Time.UtcNow;
        delivery.AttemptCount++;
        delivery.TransportStartedUtc = Time.UtcNow;
        delivery.Revision++;

        var occurrence = delivery.Occurrence;
        if (occurrence.IncidentId is not null && occurrence.Kind == NotificationOccurrenceKinds.Opening)
        {
            db.NotificationIncidentTargets.Add(new NotificationIncidentTarget
            {
                IncidentTargetId = Guid.NewGuid().ToString(),
                IncidentId = occurrence.IncidentId,
                Generation = occurrence.Generation!.Value,
                DestinationId = delivery.DestinationId,
                TargetId = delivery.TargetId,
                DeliveryIdentityRevision = delivery.DeliveryIdentityRevision,
                OpeningAcceptedUtc = Time.UtcNow,
                UpdatedUtc = Time.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Stores interval facts only. Which channels matter comes from <see cref="PublishMappingsAsync"/>.</summary>
    public async Task SetCoverageAsync(string sourceId, params (string Channel, EpgInterval[] Intervals)[] channels)
    {
        Time.Advance(TimeSpan.FromSeconds(1));
        await using var db = NewContext();
        foreach (var (channel, intervals) in channels)
        {
            var (encoded, count) = EpgCoverageFacts.EncodeIntervals(intervals, Time.GetUtcNow());
            var row = await db.EpgNotificationCoverage.FirstOrDefaultAsync(x => x.EpgSourceId == sourceId && x.XmltvChannelId == channel);
            if (row is null)
            {
                row = new EpgNotificationCoverage
                {
                    EpgNotificationCoverageId = Guid.NewGuid().ToString(), EpgSourceId = sourceId, XmltvChannelId = channel,
                };
                db.EpgNotificationCoverage.Add(row);
            }

            row.IntervalsEncoded = encoded;
            row.IntervalCount = count;
            row.UpdatedUtc = Time.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    public async Task PublishMappingsAsync(string sourceId, params string[] xmltvChannelIds) =>
        await PublishMappingsForProfileAsync("profile-1", sourceId, xmltvChannelIds);

    /// <summary>
    /// Creates a committed publication: provider channels, mappings to the source, and an active snapshot whose channel
    /// index lists them. This is what makes the XMLTV channels relevant.
    /// </summary>
    public async Task PublishMappingsForProfileAsync(string profileId, string sourceId, string[] xmltvChannelIds)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"m3u-notif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _tempDirectories.Add(directory);

        await using var db = NewContext();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        db.FetchRuns.Add(new FetchRun { FetchRunId = $"run-{suffix}", ProviderId = "provider-1", StartedUtc = Time.UtcNow, Status = "ok" });
        await db.SaveChangesAsync();

        var entries = new List<ChannelIndexEntry>();
        foreach (var xmltvId in xmltvChannelIds)
        {
            var providerChannelId = $"pc-{xmltvId}-{suffix}";
            db.ProviderChannels.Add(new ProviderChannel
            {
                ProviderChannelId = providerChannelId, ProviderId = "provider-1", DisplayName = xmltvId, TvgId = xmltvId,
                StreamUrl = $"http://x/live/{xmltvId}.ts", Active = true, ContentType = "live", LastFetchRunId = $"run-{suffix}",
                FirstSeenUtc = Time.UtcNow, LastSeenUtc = Time.UtcNow,
            });
            await db.SaveChangesAsync();
            db.EpgChannelMappings.Add(new EpgChannelMapping
            {
                EpgChannelMappingId = $"map-{providerChannelId}", ProfileId = profileId, ProviderChannelId = providerChannelId,
                EpgSourceId = sourceId, XmltvChannelId = xmltvId, CreatedUtc = Time.UtcNow, UpdatedUtc = Time.UtcNow,
            });
            entries.Add(new ChannelIndexEntry($"key-{xmltvId}", xmltvId, xmltvId, null, null, null, null, providerChannelId, $"http://x/live/{xmltvId}.ts", "provider-1"));
        }

        var indexPath = Path.Combine(directory, "channel_index.ndjson");
        await ChannelIndexStore.WriteAsync(indexPath, ChannelIndexStore.GetIdxPath(indexPath), entries, CancellationToken.None);

        Time.Advance(TimeSpan.FromSeconds(1));
        foreach (var previous in await db.Snapshots.Where(x => x.Status == "active" && x.ProfileId == profileId).ToListAsync())
            previous.Status = "retired";
        db.Snapshots.Add(new Snapshot
        {
            SnapshotId = $"snap-{suffix}", ProfileId = profileId, Status = "active", CreatedUtc = Time.UtcNow,
            ChannelIndexPath = indexPath, PlaylistPath = indexPath, XmltvPath = indexPath, StatusJsonPath = indexPath,
        });
        await db.SaveChangesAsync();
    }

    public async Task RemoveMappingsAsync()
    {
        await using var db = NewContext();
        db.EpgChannelMappings.RemoveRange(await db.EpgChannelMappings.ToListAsync());
        await db.SaveChangesAsync();
    }

    public EpgInterval Interval(double startHours, double stopHours) =>
        new(Time.GetUtcNow().AddHours(startHours), Time.GetUtcNow().AddHours(stopHours));

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await Database.DisposeAsync();
        foreach (var directory in _tempDirectories.Where(Directory.Exists))
            Directory.Delete(directory, recursive: true);
    }
}
