using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Tests.Stubs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class XtreamSeriesExpansionServiceTests
{
    [TestMethod]
    public async Task ExpandJob_PersistsEpisodesAndTriggersRefresh()
    {
        var handler = new SeriesInfoHandler
        {
            [1001] = """{"info":{"name":"Breaking Bad"},"episodes":{"1":[{"id":"1","episode_num":1,"title":"Pilot","container_extension":"mkv"}]}}""",
            [1002] = """{"info":{"name":"The Wire"},"episodes":{"1":[{"id":"2","episode_num":1,"title":"The Target","container_extension":"mkv"}]}}""",
        };

        var (service, scopeFactory, refreshTrigger) = CreateService(handler, seedProviderId: "p1");

        await service.ExpandJobAsync(Job("p1", [new(1001, 100L), new(1002, 200L)]), CancellationToken.None);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row1 = await db.XtreamSeriesCache.FindAsync("p1", 1001);
        Assert.IsNotNull(row1);
        Assert.AreEqual(100L, row1!.LastModifiedEpoch);
        Assert.Contains("Pilot", row1.EpisodesJson);

        var row2 = await db.XtreamSeriesCache.FindAsync("p1", 1002);
        Assert.IsNotNull(row2);
        Assert.AreEqual(200L, row2!.LastModifiedEpoch);

        Assert.AreEqual(1, refreshTrigger.RefreshCount, "Completion must trigger a snapshot refresh so episodes publish.");
    }

    [TestMethod]
    public async Task ExpandJob_FailedFetch_NotPersistedSoItRetriesNextSync()
    {
        var handler = new SeriesInfoHandler(); // no routes → every get_series_info 404s

        var (service, scopeFactory, refreshTrigger) = CreateService(handler, seedProviderId: "p1");

        await service.ExpandJobAsync(Job("p1", [new(1001, 100L)]), CancellationToken.None);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.IsNull(await db.XtreamSeriesCache.FindAsync("p1", 1001),
            "Failed get_series_info must not be cached — it would never retry.");
        Assert.AreEqual(0, refreshTrigger.RefreshCount, "Nothing saved — no refresh should be triggered.");
    }

    [TestMethod]
    public async Task ExpandJob_UpdatesExistingCacheEntry()
    {
        var handler = new SeriesInfoHandler
        {
            [1001] = """{"info":{"name":"Breaking Bad"},"episodes":{"1":[{"id":"1","episode_num":1,"title":"Pilot","container_extension":"mkv"},{"id":"2","episode_num":2,"title":"New Episode","container_extension":"mkv"}]}}""",
        };

        var (service, scopeFactory, _) = CreateService(handler, seedProviderId: "p1");

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.XtreamSeriesCache.Add(new XtreamSeriesCache
            {
                ProviderId = "p1",
                SeriesId = 1001,
                LastModifiedEpoch = 100L,
                EpisodesJson = "{}",
            });
            await db.SaveChangesAsync();
        }

        await service.ExpandJobAsync(Job("p1", [new(1001, 999L)]), CancellationToken.None);

        await using var verifyScope = scopeFactory.CreateAsyncScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await ctx.XtreamSeriesCache.FindAsync("p1", 1001);
        Assert.AreEqual(999L, row!.LastModifiedEpoch);
        Assert.Contains("New Episode", row.EpisodesJson);
    }

    [TestMethod]
    public void TryEnqueue_SameProviderTwice_SecondIsDropped()
    {
        var (service, _, _) = CreateService(new SeriesInfoHandler(), seedProviderId: null);

        Assert.IsTrue(service.TryEnqueue(Job("p1", [new(1, 1L)])));
        Assert.IsFalse(service.TryEnqueue(Job("p1", [new(2, 2L)])),
            "Duplicate provider jobs must be deduped — the remainder is re-derived next refresh.");
        Assert.IsTrue(service.TryEnqueue(Job("p2", [new(3, 3L)])));
    }

    [TestMethod]
    public async Task TryExpandInline_CompletesWithinBudget_PersistsAndReturnsAllWithNothingQueued()
    {
        var handler = new SeriesInfoHandler
        {
            [1001] = """{"info":{"name":"Breaking Bad"},"episodes":{"1":[{"id":"1","episode_num":1,"title":"Pilot","container_extension":"mkv"}]}}""",
        };

        var (service, scopeFactory, _) = CreateService(handler, seedProviderId: "p1");

        var result = await service.TryExpandInlineAsync(
            Job("p1", [new(1001, 100L)]), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.HasCount(1, result);
        Assert.AreEqual(1001, result[0].SeriesId);
        Assert.AreEqual(0, service.WaitingJobs, "Fully expanded inline — nothing should go to the background.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.IsNotNull(await db.XtreamSeriesCache.FindAsync("p1", 1001));

        // Provider slot must be released so future syncs can expand again.
        Assert.IsTrue(service.TryEnqueue(Job("p1", [new(2, 2L)])));
    }

    [TestMethod]
    public async Task TryExpandInline_BudgetExhausted_QueuesRemainderForBackground()
    {
        var (service, _, _) = CreateService(new SeriesInfoHandler(), seedProviderId: "p1");

        // Zero budget — deadline is already past, so nothing is fetched inline.
        var result = await service.TryExpandInlineAsync(
            Job("p1", [new(1001, 100L), new(1002, 200L)]), TimeSpan.Zero, CancellationToken.None);

        Assert.IsEmpty(result);
        Assert.AreEqual(1, service.WaitingJobs, "Unfinished remainder must be queued for the background worker.");
        Assert.IsFalse(service.TryEnqueue(Job("p1", [new(3, 3L)])),
            "Provider stays deduped while its remainder job is waiting.");
    }

    [TestMethod]
    public async Task TryExpandInline_ProviderAlreadyQueued_SkipsWithoutFetching()
    {
        var handler = new SeriesInfoHandler
        {
            [1001] = """{"info":{},"episodes":{}}""",
        };

        var (service, scopeFactory, _) = CreateService(handler, seedProviderId: "p1");
        Assert.IsTrue(service.TryEnqueue(Job("p1", [new(1001, 100L)])));

        var result = await service.TryExpandInlineAsync(
            Job("p1", [new(1001, 100L)]), TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.IsEmpty(result, "Inline expansion must not double-fetch a provider that already has a job.");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.IsNull(await db.XtreamSeriesCache.FindAsync("p1", 1001));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static XtreamSeriesExpansionJob Job(string providerId, List<XtreamSeriesStub> series)
        => new(providerId, providerId, "http://panel.test:8080", "user", "pass", 5, series);

    private static (XtreamSeriesExpansionService Service, IServiceScopeFactory ScopeFactory, RecordingRefreshTrigger RefreshTrigger) CreateService(
        HttpMessageHandler handler, string? seedProviderId)
    {
        var scopeFactory = XtreamLineupClientTests.CreateScopeFactory(seedProviderId);
        var refreshTrigger = new RecordingRefreshTrigger();

        var service = new XtreamSeriesExpansionService(
            new FakeHttpClientFactory(handler),
            scopeFactory,
            refreshTrigger,
            new NullEventService(),
            new HeavyWorkGate(),
            NullLogger<XtreamSeriesExpansionService>.Instance);

        return (service, scopeFactory, refreshTrigger);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // Routes get_series_info requests by series_id; everything else (and unknown ids) 404s.
    private sealed class SeriesInfoHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, string> _bySeriesId = [];

        public string this[int seriesId]
        {
            set => _bySeriesId[seriesId] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = request.RequestUri!.Query;
            var idPart = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(p => p.StartsWith("series_id=", StringComparison.Ordinal));

            if (idPart is not null
                && int.TryParse(idPart["series_id=".Length..], out var seriesId)
                && _bySeriesId.TryGetValue(seriesId, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
