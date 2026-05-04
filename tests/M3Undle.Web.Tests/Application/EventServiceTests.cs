using M3Undle.Web.Application;
using M3Undle.Web.Components.Layout;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MudBlazor;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class EventServiceTests
{
    [TestMethod]
    public async Task PublishAsync_DedupesProviderScopedEventsAndReportsHighestSeverity()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = fixture.Services.GetRequiredService<IEventService>();

        await service.PublishAsync(
            SystemEventSeverity.Error,
            SystemEventTypes.ProviderFetchFailed,
            "Provider failed",
            "first",
            providerId: "provider-1");
        await service.PublishAsync(
            SystemEventSeverity.Error,
            SystemEventTypes.ProviderFetchFailed,
            "Provider still failed",
            "second",
            providerId: "provider-1");
        await service.PublishAsync(
            SystemEventSeverity.Info,
            SystemEventTypes.AppRestarted,
            "Application started");

        var events = await service.GetAllAsync();
        var failure = events.Single(e => e.EventType == SystemEventTypes.ProviderFetchFailed);
        var summary = await service.GetSummaryAsync();

        Assert.HasCount(2, events);
        Assert.AreEqual(2, failure.OccurrenceCount);
        Assert.AreEqual("Provider still failed", failure.Title);
        Assert.AreEqual("second", failure.Detail);
        Assert.AreEqual(new SystemEventSummary(2, SystemEventSeverity.Error), summary);
    }

    [TestMethod]
    public async Task PublishAsync_NewProviderFailureClearsPriorBackOnlineEvent()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = fixture.Services.GetRequiredService<IEventService>();

        await service.PublishAsync(
            SystemEventSeverity.Error,
            SystemEventTypes.ProviderFetchFailed,
            "Provider failed",
            providerId: "provider-1");
        await service.PublishAsync(
            SystemEventSeverity.Info,
            SystemEventTypes.ProviderBackOnline,
            "Provider recovered",
            providerId: "provider-1");

        Assert.IsTrue(await service.HasEventAsync(SystemEventTypes.ProviderBackOnline, providerId: "provider-1"));

        await service.PublishAsync(
            SystemEventSeverity.Error,
            SystemEventTypes.ProviderFetchFailed,
            "Provider failed again",
            providerId: "provider-1");

        Assert.IsFalse(await service.HasEventAsync(SystemEventTypes.ProviderBackOnline, providerId: "provider-1"));
        Assert.IsTrue(await service.HasEventAsync(SystemEventTypes.ProviderFetchFailed, providerId: "provider-1"));
    }

    [TestMethod]
    public async Task PublishAsync_StreamRecoveryAndUnstableEventsReplaceEachOtherPerProvider()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = fixture.Services.GetRequiredService<IEventService>();

        await service.PublishAsync(
            SystemEventSeverity.Warning,
            SystemEventTypes.ProviderStreamUnstable,
            "Provider stream unstable",
            providerId: "provider-1");

        Assert.IsTrue(await service.HasEventAsync(SystemEventTypes.ProviderStreamUnstable, providerId: "provider-1"));

        await service.PublishAsync(
            SystemEventSeverity.Info,
            SystemEventTypes.ProviderStreamRecovered,
            "Provider stream recovered",
            providerId: "provider-1");

        Assert.IsFalse(await service.HasEventAsync(SystemEventTypes.ProviderStreamUnstable, providerId: "provider-1"));
        Assert.IsTrue(await service.HasEventAsync(SystemEventTypes.ProviderStreamRecovered, providerId: "provider-1"));

        await service.PublishAsync(
            SystemEventSeverity.Warning,
            SystemEventTypes.ProviderStreamUnstable,
            "Provider stream unstable again",
            providerId: "provider-1");

        Assert.IsTrue(await service.HasEventAsync(SystemEventTypes.ProviderStreamUnstable, providerId: "provider-1"));
        Assert.IsFalse(await service.HasEventAsync(SystemEventTypes.ProviderStreamRecovered, providerId: "provider-1"));
    }

    [TestMethod]
    public async Task CleanupOldEventsAsync_DeletesOnlyEventsOlderThanRetention()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = fixture.Services.GetRequiredService<IEventService>();

        await using (var db = fixture.CreateDbContext())
        {
            var settings = await db.SiteSettings.SingleAsync();
            settings.EventRetentionDays = 7;
            db.SystemEvents.AddRange(
                NewEvent("old", SystemEventTypes.AppRestarted, SystemEventSeverity.Info, DateTime.UtcNow.AddDays(-8)),
                NewEvent("new", SystemEventTypes.LoginFailed, SystemEventSeverity.Warning, DateTime.UtcNow.AddDays(-6)));
            await db.SaveChangesAsync();
        }

        await service.CleanupOldEventsAsync();

        await using var verify = fixture.CreateDbContext();
        var remaining = await verify.SystemEvents.SingleAsync();
        Assert.AreEqual("new", remaining.SystemEventId);
    }

    [TestMethod]
    public async Task SetRetentionDaysAsync_RejectsOutOfRangeAndPersistsValidValue()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = fixture.Services.GetRequiredService<IEventService>();

        await AssertThrowsAsync<ArgumentOutOfRangeException>(() => service.SetRetentionDaysAsync(0));
        await AssertThrowsAsync<ArgumentOutOfRangeException>(() => service.SetRetentionDaysAsync(366));

        await service.SetRetentionDaysAsync(30);

        Assert.AreEqual(30, await service.GetRetentionDaysAsync());
    }

    [TestMethod]
    public void BadgePresentation_MapsHighestSeverityToExpectedColor()
    {
        Assert.AreEqual(Color.Default, SystemEventBadgePresentation.ColorFor(new SystemEventSummary(0, null)));
        Assert.AreEqual(Color.Info, SystemEventBadgePresentation.ColorFor(new SystemEventSummary(1, SystemEventSeverity.Info)));
        Assert.AreEqual(Color.Warning, SystemEventBadgePresentation.ColorFor(new SystemEventSummary(1, SystemEventSeverity.Warning)));
        Assert.AreEqual(Color.Error, SystemEventBadgePresentation.ColorFor(new SystemEventSummary(1, SystemEventSeverity.Error)));
    }

    private static SystemEvent NewEvent(string id, string eventType, SystemEventSeverity severity, DateTime occurredAt) => new()
    {
        SystemEventId = id,
        EventType = eventType,
        Severity = severity.ToString(),
        Title = id,
        OccurredAt = occurredAt,
        OccurrenceCount = 1,
    };

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
            Assert.Fail($"Expected {typeof(TException).Name} to be thrown.");
        }
        catch (TException)
        {
            // Expected path.
        }
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<AppEventBus>();
        services.AddSingleton<IEventService, EventService>();
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        return new TestFixture(connection, provider);
    }

    private sealed class TestFixture(SqliteConnection connection, ServiceProvider services) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;

        public ApplicationDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options);

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
