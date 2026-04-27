using M3Undle.Web.Application;
using M3Undle.Web.Application.Downstream;
using M3Undle.Web.Tests.Stubs;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class DownstreamNotificationServiceTests
{
    [TestMethod]
    public async Task RefreshCompleted_FirstRun_NotifiesGuideOnlyIntegration()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-1",
                Name = "Guide Only",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                TriggerOnLineupUpdate = false,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new RecordingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: null);
        await adapter.WaitForCountAsync(1, CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, adapter.CallCount);
    }

    [TestMethod]
    public async Task RefreshCompleted_NoMeaningfulChange_DoesNotNotify()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-none",
                Name = "No Change",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new RecordingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.None);
        await Task.Delay(200);

        await service.StopAsync(CancellationToken.None);
        Assert.AreEqual(0, adapter.CallCount);
    }

    [TestMethod]
    public async Task RefreshCompleted_WhenAdapterReturnsError_PersistsLastNotifyError()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-error",
                Name = "Failure Adapter",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new ErrorAdapter("webhook", "simulated downstream failure");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        var eventService = new RecordingEventService();
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            eventService,
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.Lineup);
        await adapter.WaitForCountAsync(1, CancellationToken.None);
        await WaitUntilAsync(async () =>
        {
            await using var verify = fixture.CreateDbContext();
            var row = await verify.DownstreamIntegrations.SingleAsync(x => x.DownstreamIntegrationId == "int-error");
            return row.LastNotifyError == "simulated downstream failure";
        }, TimeSpan.FromSeconds(3));

        await service.StopAsync(CancellationToken.None);

        await using (var verify = fixture.CreateDbContext())
        {
            var row = await verify.DownstreamIntegrations.SingleAsync(x => x.DownstreamIntegrationId == "int-error");
            Assert.AreEqual("simulated downstream failure", row.LastNotifyError);
            Assert.IsNull(row.LastNotifiedUtc);
        }

        Assert.HasCount(1, eventService.PublishedEvents);
        var published = eventService.PublishedEvents.Single();
        Assert.AreEqual(SystemEventTypes.DownstreamNotificationFailed, published.EventType);
        Assert.AreEqual("int-error", published.IntegrationId);
        Assert.AreEqual("simulated downstream failure", published.Detail);
    }

    [TestMethod]
    public async Task RefreshCompleted_WhenApiKeyDecryptFails_WritesDecryptErrorAndSkipsAdapter()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-decrypt",
                Name = "Decrypt Failure",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                ApiKeyEncrypted = "not-valid-base64",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new RecordingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.Lineup);
        await WaitUntilAsync(async () =>
        {
            await using var verify = fixture.CreateDbContext();
            var row = await verify.DownstreamIntegrations.SingleAsync(x => x.DownstreamIntegrationId == "int-decrypt");
            return row.LastNotifyError is not null;
        }, TimeSpan.FromSeconds(3));

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, adapter.CallCount);
        await using (var verify = fixture.CreateDbContext())
        {
            var row = await verify.DownstreamIntegrations.SingleAsync(x => x.DownstreamIntegrationId == "int-decrypt");
            Assert.IsNotNull(row.LastNotifyError);
            StringAssert.Contains(row.LastNotifyError, "Failed to decrypt API key:");
        }
    }

    [TestMethod]
    public async Task RefreshCompleted_NotifiesMatchingIntegrationsConcurrently()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-a",
                Name = "Concurrent A",
                Kind = "webhook",
                BaseUrl = "http://localhost/a",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = false,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-b",
                Name = "Concurrent B",
                Kind = "webhook",
                BaseUrl = "http://localhost/b",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = false,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new ConcurrencyAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.Lineup);
        await adapter.WaitForTwoStartedAsync(CancellationToken.None);
        adapter.ReleaseAll();
        await Task.Delay(150);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(2, adapter.CallCount);
        Assert.IsGreaterThanOrEqualTo(adapter.MaxInFlight, 2, $"Expected concurrent notifications, max in-flight was {adapter.MaxInFlight}.");
        await using (var verify = fixture.CreateDbContext())
        {
            var rows = await verify.DownstreamIntegrations
                .Where(x => x.DownstreamIntegrationId == "int-a" || x.DownstreamIntegrationId == "int-b")
                .ToListAsync();
            Assert.HasCount(2, rows);
            Assert.IsTrue(rows.All(x => x.LastNotifiedUtc.HasValue && x.LastNotifyError is null));
        }
    }

    // -------------------------------------------------------------------------
    // Trigger type filtering: lineup-only vs guide-only integrations
    // -------------------------------------------------------------------------

    /// <summary>
    /// A guide-only change (ChangeClasses.GuideOnly) must NOT fire an integration whose
    /// trigger is lineup-only (TriggerOnGuideUpdate=false, TriggerOnLineupUpdate=true).
    /// </summary>
    [TestMethod]
    public async Task RefreshCompleted_GuideOnlyChange_DoesNotNotifyLineupOnlyIntegration()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-lineup-only",
                Name = "Lineup Only",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = false,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new RecordingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        // Guide-only change — lineup-only integration must be suppressed.
        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.GuideOnly);
        await Task.Delay(200);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, adapter.CallCount,
            "A guide-only change must not trigger a lineup-only integration.");
    }

    /// <summary>
    /// A lineup change (ChangeClasses.Lineup) must NOT fire an integration whose
    /// trigger is guide-only (TriggerOnGuideUpdate=true, TriggerOnLineupUpdate=false).
    /// </summary>
    [TestMethod]
    public async Task RefreshCompleted_LineupChange_DoesNotNotifyGuideOnlyIntegration()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-guide-only",
                Name = "Guide Only",
                Kind = "webhook",
                BaseUrl = "http://localhost/hook",
                TriggerOnLineupUpdate = false,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new RecordingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        // Lineup change — guide-only integration must be suppressed.
        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.Lineup);
        await Task.Delay(200);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, adapter.CallCount,
            "A lineup change must not trigger a guide-only integration.");
    }

    /// <summary>
    /// A guide-only change fires a guide-capable integration but not a lineup-only one,
    /// even when both are enabled in the same run.
    /// </summary>
    [TestMethod]
    public async Task RefreshCompleted_GuideOnlyChange_FiresGuideIntegration_SkipsLineupIntegration()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-guide",
                Name = "Guide Capable",
                Kind = "webhook",
                BaseUrl = "http://localhost/guide",
                TriggerOnLineupUpdate = false,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-lineup",
                Name = "Lineup Only",
                Kind = "webhook",
                BaseUrl = "http://localhost/lineup",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = false,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new TrackingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true, changeClass: ChangeClasses.GuideOnly);
        await adapter.WaitForCountAsync(1, CancellationToken.None);
        await Task.Delay(100); // let any extra calls arrive

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, adapter.CallCount,
            "Exactly one integration (guide-capable) must fire on a guide-only change.");
        CollectionAssert.Contains(adapter.CalledUrls, "http://localhost/guide");
        CollectionAssert.DoesNotContain(adapter.CalledUrls, "http://localhost/lineup");
    }

    // -------------------------------------------------------------------------
    // Profile-scoped filtering: AffectedProfileIds on AppEvent
    // -------------------------------------------------------------------------

    /// <summary>
    /// A profile-scoped integration (ProfileId set) is silenced when the refresh
    /// did not affect that profile. Global integrations (ProfileId null) always fire.
    /// </summary>
    [TestMethod]
    public async Task RefreshCompleted_ProfileScopedIntegration_SilencedWhenProfileNotAffected()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            // Seed profile rows first — SQLite FK checks require the referenced row to exist
            setup.Profiles.Add(SeedProfile("profile-a"));
            setup.Profiles.Add(SeedProfile("profile-b"));
            await setup.SaveChangesAsync();

            // Global integration (no profile) — must fire
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-global",
                Name = "Global",
                Kind = "webhook",
                BaseUrl = "http://localhost/global",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            // Profile-scoped integration for profile-B — must be suppressed
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-profile-b",
                Name = "Profile B Only",
                Kind = "webhook",
                BaseUrl = "http://localhost/profile-b",
                ProfileId = "profile-b",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new TrackingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        // Only profile-A was affected this run — profile-B integration must be silent
        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true,
            changeClass: ChangeClasses.Lineup,
            affectedProfileIds: new HashSet<string> { "profile-a" });

        await adapter.WaitForCountAsync(1, CancellationToken.None);
        await Task.Delay(100);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, adapter.CallCount,
            "Only the global (null-ProfileId) integration should fire.");
        CollectionAssert.Contains(adapter.CalledUrls, "http://localhost/global");
        CollectionAssert.DoesNotContain(adapter.CalledUrls, "http://localhost/profile-b");
    }

    /// <summary>
    /// A profile-scoped integration fires when the refresh included that profile.
    /// </summary>
    [TestMethod]
    public async Task RefreshCompleted_ProfileScopedIntegration_FiresWhenProfileIsAffected()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(SeedProfile("profile-a"));
            await setup.SaveChangesAsync();
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-profile-a",
                Name = "Profile A Only",
                Kind = "webhook",
                BaseUrl = "http://localhost/profile-a",
                ProfileId = "profile-a",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new TrackingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        // profile-a was affected — its scoped integration must fire
        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true,
            changeClass: ChangeClasses.Lineup,
            affectedProfileIds: new HashSet<string> { "profile-a" });

        await adapter.WaitForCountAsync(1, CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, adapter.CallCount,
            "A profile-scoped integration must fire when its profile was affected.");
        CollectionAssert.Contains(adapter.CalledUrls, "http://localhost/profile-a");
    }

    /// <summary>
    /// When AffectedProfileIds is null (legacy / unknown), all enabled integrations fire
    /// regardless of their ProfileId — preserving backward compatibility.
    /// </summary>
    [TestMethod]
    public async Task RefreshCompleted_NullAffectedProfileIds_FiresAllIntegrations()
    {
        await using var fixture = await CreateFixtureAsync();

        await using (var setup = fixture.CreateDbContext())
        {
            setup.Profiles.Add(SeedProfile("profile-x"));
            await setup.SaveChangesAsync();
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-global",
                Name = "Global",
                Kind = "webhook",
                BaseUrl = "http://localhost/global",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            setup.DownstreamIntegrations.Add(new DownstreamIntegration
            {
                DownstreamIntegrationId = "int-profile-x",
                Name = "Profile X",
                Kind = "webhook",
                BaseUrl = "http://localhost/profile-x",
                ProfileId = "profile-x",
                TriggerOnLineupUpdate = true,
                TriggerOnGuideUpdate = true,
                Enabled = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await setup.SaveChangesAsync();
        }

        var eventBus = new AppEventBus();
        var adapter = new TrackingAdapter("webhook");
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        using var service = new DownstreamNotificationService(
            eventBus,
            fixture.ScopeFactory,
            [adapter],
            encryption,
            new NullEventService(),
            NullLogger<DownstreamNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        // affectedProfileIds = null — legacy path, must fire all
        eventBus.Publish(AppEventKind.RefreshCompleted, succeeded: true,
            changeClass: ChangeClasses.Lineup,
            affectedProfileIds: null);

        await adapter.WaitForCountAsync(2, CancellationToken.None);

        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(2, adapter.CallCount,
            "When AffectedProfileIds is null every enabled integration must fire.");
        CollectionAssert.Contains(adapter.CalledUrls, "http://localhost/global");
        CollectionAssert.Contains(adapter.CalledUrls, "http://localhost/profile-x");
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        // Use a named shared-cache in-memory database so that each EF Core scope opens
        // its own independent SqliteConnection rather than sharing a single connection
        // object.  Sharing a single connection causes SQLite Error 5 when a background
        // scope tries to register custom functions while the test's polling loop holds
        // an active reader on the same connection.
        //
        // A keep-alive connection is opened for the lifetime of the fixture; without it
        // SQLite destroys the named in-memory database as soon as the last connection
        // referencing it closes.
        var dbName = $"test-{Guid.NewGuid():N}";
        var cs = $"Data Source={dbName};Mode=Memory;Cache=Shared";

        var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(cs));
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return new TestFixture(keepAlive, cs, provider);
    }

    private sealed class TestFixture(
        SqliteConnection keepAlive,
        string connectionString,
        ServiceProvider provider) : IAsyncDisposable
    {
        public IServiceScopeFactory ScopeFactory => provider.GetRequiredService<IServiceScopeFactory>();

        // Creates a DbContext directly from options — no DI scope, so no scope leak and
        // no shared connection object. The caller owns the lifetime via `await using`.
        public ApplicationDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connectionString)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await keepAlive.DisposeAsync();
        }
    }

    private sealed class RecordingAdapter(string kind) : IDownstreamAdapter
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => kind;
        public int CallCount { get; private set; }

        public Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        {
            CallCount++;
            _called.TrySetResult();
            return Task.FromResult<string?>(null);
        }

        public async Task WaitForCountAsync(int expected, CancellationToken ct)
        {
            while (CallCount < expected)
            {
                await _called.Task.WaitAsync(ct);
                if (CallCount >= expected)
                    return;
            }
        }
    }

    private sealed class ErrorAdapter(string kind, string error) : IDownstreamAdapter
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Kind => kind;
        public int CallCount { get; private set; }

        public Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        {
            CallCount++;
            _called.TrySetResult();
            return Task.FromResult<string?>(error);
        }

        public Task WaitForCountAsync(int expected, CancellationToken ct)
            => WaitUntilCountAsync(expected, ct);

        private async Task WaitUntilCountAsync(int expected, CancellationToken ct)
        {
            while (CallCount < expected)
            {
                await _called.Task.WaitAsync(ct);
                if (CallCount >= expected)
                    return;
            }
        }
    }

    private sealed class ConcurrencyAdapter(string kind) : IDownstreamAdapter
    {
        private readonly TaskCompletionSource _twoStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private int _callCount;
        private int _maxInFlight;

        public string Kind => kind;
        public int CallCount => Volatile.Read(ref _callCount);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            var nowInFlight = Interlocked.Increment(ref _inFlight);
            UpdateMaxInFlight(nowInFlight);

            if (CallCount >= 2)
                _twoStarted.TrySetResult();

            try
            {
                await _twoStarted.Task.WaitAsync(ct);
                await _release.Task.WaitAsync(ct);
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task WaitForTwoStartedAsync(CancellationToken ct)
            => _twoStarted.Task.WaitAsync(ct);

        public void ReleaseAll()
            => _release.TrySetResult();

        private void UpdateMaxInFlight(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxInFlight);
                if (candidate <= current)
                    return;

                if (Interlocked.CompareExchange(ref _maxInFlight, candidate, current) == current)
                    return;
            }
        }
    }

    /// <summary>
    /// Records every (baseUrl, trigger) pair that was notified. Used to verify which
    /// specific integrations fired rather than just a total count.
    /// </summary>
    private sealed class TrackingAdapter(string kind) : IDownstreamAdapter
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _urls = [];
        private int _callCount;

        public string Kind => kind;
        public int CallCount => Volatile.Read(ref _callCount);
        public List<string> CalledUrls { get { lock (_urls) return [.. _urls]; } }

        public Task<string?> NotifyAsync(string baseUrl, string? apiKey, string? webhookHeadersJson, DownstreamTrigger trigger, CancellationToken ct)
        {
            lock (_urls) _urls.Add(baseUrl);
            Interlocked.Increment(ref _callCount);
            _called.TrySetResult();
            return Task.FromResult<string?>(null);
        }

        public async Task WaitForCountAsync(int expected, CancellationToken ct)
        {
            while (CallCount < expected)
            {
                await _called.Task.WaitAsync(ct);
                if (CallCount >= expected)
                    return;
            }
        }
    }

    private sealed record PublishedEvent(
        SystemEventSeverity Severity,
        string EventType,
        string Title,
        string? Detail,
        string? ProviderId,
        string? IntegrationId);

    private sealed class RecordingEventService : IEventService
    {
        private readonly List<PublishedEvent> _publishedEvents = [];

        public IReadOnlyList<PublishedEvent> PublishedEvents
        {
            get
            {
                lock (_publishedEvents)
                    return [.. _publishedEvents];
            }
        }

        public Task PublishAsync(SystemEventSeverity severity, string eventType, string title, string? detail = null, string? providerId = null, string? integrationId = null)
        {
            lock (_publishedEvents)
                _publishedEvents.Add(new PublishedEvent(severity, eventType, title, detail, providerId, integrationId));

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SystemEvent>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SystemEvent>>([]);

        public Task<int> GetCountAsync(CancellationToken ct = default)
            => Task.FromResult(0);

        public Task<SystemEventSummary> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new SystemEventSummary(0, null));

        public Task DismissAsync(string eventId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DismissAllAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task CleanupOldEventsAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> HasEventAsync(string eventType, string? providerId = null, string? integrationId = null, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<int> GetRetentionDaysAsync(CancellationToken ct = default)
            => Task.FromResult(SystemEventSettings.DefaultRetentionDays);

        public Task SetRetentionDaysAsync(int days, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static Profile SeedProfile(string profileId) => new()
    {
        ProfileId = profileId,
        Name = profileId,
        Enabled = true,
        IsActive = false,
        OutputName = profileId,
        MergeMode = "merge",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(30);
        }

        Assert.Fail("Timed out waiting for expected condition.");
    }
}
