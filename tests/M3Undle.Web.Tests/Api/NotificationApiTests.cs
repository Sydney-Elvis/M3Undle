using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using M3Undle.Web.Contracts.Notifications;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Api;

// The host reads process-wide environment variables (encryption key, UI authentication), so this must not run alongside other tests.
[TestClass]
[DoNotParallelize]
public sealed class NotificationApiTests
{
    private const string Root = "/api/v1/notifications";

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _previous = [];

        public EnvScope(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static EnvScope OpenInstance() => new(
        ("M3UNDLE_ENCRYPTION_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
        ("M3UNDLE_ENCRYPTION_KEYS", null),
        ("M3UNDLE_AUTH_ENABLED", "false"));

    private static HttpRequestMessage Mutation(HttpMethod method, string url, object body, bool csrf = true)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        if (csrf)
            request.Headers.Add("X-Requested-With", "test");
        return request;
    }

    private static async Task<NotificationsOverviewResponse> OverviewAsync(HttpClient client)
        => (await client.GetFromJsonAsync<NotificationsOverviewResponse>(Root, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;

    [TestMethod]
    public async Task Overview_ListsTheWholeCatalogOff_WithNothingEnabled_AndNoSecrets()
    {
        using var env = OpenInstance();
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var overview = await OverviewAsync(client);

        Assert.HasCount(12, overview.Routes);
        Assert.IsTrue(overview.Routes.All(r => r.DestinationKind is null), "Every row starts Off.");
        Assert.AreEqual(6, overview.Routes.Count(r => r.IsIncident), "Failure/recovery pairs are one row each.");
        Assert.IsTrue(overview.Destinations.All(d => !d.Enabled));
        Assert.IsFalse(overview.Settings.SendingEnabled);
        Assert.IsTrue(overview.Routes.All(r => r.Available), "Every producer is implemented and tested.");
        CollectionAssert.AreEquivalent(new[] { "matrix", "smtp" }, overview.Destinations.Select(d => d.Kind).ToArray());
    }

    [TestMethod]
    public async Task Mutations_RequireTheCsrfHeader_AndReadsDoNot()
    {
        using var env = OpenInstance();
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var overview = await OverviewAsync(client);

        var smtp = new NotificationDestinationRequest(
            overview.Destinations.Single(d => d.Kind == "smtp").ConfigRevision,
            new SmtpDestinationRequest("smtp.example.org", 587, "starttls", "none", null, null, false, "m3undle@example.org", null, ["a@example.org"]), null);

        using var blocked = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/destinations/smtp", smtp, csrf: false));
        Assert.AreEqual(HttpStatusCode.BadRequest, blocked.StatusCode, "A request without the header is rejected before it reaches the service.");

        using var allowed = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/destinations/smtp", smtp));
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode, await allowed.Content.ReadAsStringAsync());

        using var read = await client.GetAsync($"{Root}/deliveries");
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
    }

    [TestMethod]
    public async Task Secrets_AreWriteOnly_NeverEchoedByAnyResponse()
    {
        using var env = OpenInstance();
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var revision = (await OverviewAsync(client)).Destinations.Single(d => d.Kind == "smtp").ConfigRevision;

        var save = new NotificationDestinationRequest(revision,
            new SmtpDestinationRequest("smtp.example.org", 587, "starttls", "password", "mailer", "Sup3r Secret pw ", false, "m3undle@example.org", null, ["a@example.org"]), null);
        using var response = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/destinations/smtp", save));
        var saveBody = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, saveBody);
        Assert.DoesNotContain("Sup3r", saveBody);

        var raw = await client.GetStringAsync(Root);
        Assert.DoesNotContain("Sup3r", raw);
        Assert.DoesNotContain("m3e:v2", raw, "Ciphertext is not exposed either.");
        var overview = JsonSerializer.Deserialize<NotificationsOverviewResponse>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var dto = overview.Destinations.Single(d => d.Kind == "smtp");
        Assert.IsTrue(dto.Smtp!.HasPassword);
        Assert.AreEqual("mailer", dto.Smtp.Username);
        CollectionAssert.AreEqual(new[] { "a@example.org" }, dto.Smtp.Recipients.ToArray());
        Assert.IsFalse(dto.VerifiedCurrent);
        Assert.IsFalse(dto.Enabled);
    }

    [TestMethod]
    public async Task StaleRevisions_UnknownKeys_UnavailableRows_AndInvalidInput_AreReportedPrecisely()
    {
        using var env = OpenInstance();
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var overview = await OverviewAsync(client);
        var settings = overview.Settings;

        NotificationSettingsRequest Settings(int revision, int warn = 12, int recover = 14) => new(
            revision, false, false, settings.FailureDelayMinutes, settings.OverdueGraceMinutes, settings.ReminderIntervalHours, warn,
            settings.CoverageWarnPercent, recover, settings.CoverageRecoverPercent, settings.CoverageGapMinutes, settings.RetentionDays);

        using var stale = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/settings", Settings(settings.Revision + 5)));
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);

        using var invalid = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/settings", Settings(settings.Revision, warn: 40, recover: 20)));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        StringAssert.Contains(await invalid.Content.ReadAsStringAsync(), "coverageRecoverHours");

        using var unknown = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/routes/matrix.message", new NotificationRouteRequest(1, null)));
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);

        using var unusable = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/routes/epg.fetch_failed", new NotificationRouteRequest(1, "smtp")));
        Assert.AreEqual(HttpStatusCode.Conflict, unusable.StatusCode, "A method that is not enabled and tested is not selectable.");

        using var badKind = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/destinations/pigeon", new NotificationDestinationRequest(1, null, null)));
        Assert.AreEqual(HttpStatusCode.NotFound, badKind.StatusCode);

        using var enableEarly = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/destinations/smtp/enabled", new NotificationEnabledRequest(1, true)));
        Assert.AreEqual(HttpStatusCode.BadRequest, enableEarly.StatusCode, "An unfinished setup cannot be enabled.");
    }

    [TestMethod]
    public async Task TestSends_AreRateLimited_AndReportTheTransportOutcomeSafely()
    {
        using var env = OpenInstance();
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var revision = (await OverviewAsync(client)).Destinations.Single(d => d.Kind == "smtp").ConfigRevision;

        // A closed local port: the real transport fails fast and safely.
        var save = new NotificationDestinationRequest(revision,
            new SmtpDestinationRequest("127.0.0.1", 1, "starttls", "none", null, null, false, "m3undle@example.org", null, ["a@example.org"]), null);
        using var saved = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/destinations/smtp", save));
        var newRevision = (await saved.Content.ReadFromJsonAsync<NotificationChangeResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.Revision!.Value;

        using var first = await client.SendAsync(Mutation(HttpMethod.Post, $"{Root}/destinations/smtp/test", new NotificationRevisionRequest(newRevision)));
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        var result = (await first.Content.ReadFromJsonAsync<NotificationTestResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.IsFalse(result.VerificationApplied);
        Assert.AreEqual("Failed", result.VerificationStatus);
        Assert.AreEqual("network", result.Targets.Single().ErrorCode);

        using var second = await client.SendAsync(Mutation(HttpMethod.Post, $"{Root}/destinations/smtp/test", new NotificationRevisionRequest(newRevision)));
        Assert.AreEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.IsTrue(second.Headers.RetryAfter is not null, "429 tells the client when to retry.");

        using var stale = await client.SendAsync(Mutation(HttpMethod.Post, $"{Root}/destinations/smtp/test", new NotificationRevisionRequest(newRevision + 99)));
        Assert.IsTrue(stale.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Conflict);
    }

    [TestMethod]
    public async Task Deliveries_AreBoundedFilteredAndValidated_AndRetryDismissEnforceTheirRules()
    {
        using var env = OpenInstance();
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _ = await OverviewAsync(client);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var destinationId = (await db.NotificationDestinations.FirstAsync(d => d.Kind == "smtp")).DestinationId;
            var now = DateTime.UtcNow;
            db.NotificationOccurrences.Add(new NotificationOccurrence
            {
                OccurrenceId = "o1", OccurrenceKey = "o1", NotificationKey = "epg.fetch_failed", Kind = "Opening", Title = "Guide failing",
                Body = "body", Severity = "Warning", OccurredUtc = now, CreatedUtc = now,
            });
            await db.SaveChangesAsync();
            for (var i = 0; i < 130; i++)
            {
                db.NotificationDeliveries.Add(new NotificationDelivery
                {
                    DeliveryId = $"d{i:000}", OccurrenceId = "o1", DestinationId = destinationId, ProviderKind = "smtp", TargetId = $"t{i}", TargetLabel = $"user{i}@example.org",
                    DeliveryIdentityRevision = 1, ConfigRevision = 1, State = i % 3 == 0 ? NotificationDeliveryStates.Failed : i % 3 == 1 ? NotificationDeliveryStates.Uncertain : NotificationDeliveryStates.Accepted,
                    PayloadTitle = "Guide failing", PayloadBody = "secret-looking body", MessageId = $"<{i}@x>", ErrorCode = "x", CreatedUtc = now.AddSeconds(i), UpdatedUtc = now.AddSeconds(i), DueUtc = now,
                });
            }
            await db.SaveChangesAsync();
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var page = (await client.GetFromJsonAsync<NotificationDeliveryPageResponse>($"{Root}/deliveries?pageSize=5000", options))!;
        Assert.AreEqual(130, page.Total);
        Assert.AreEqual(NotificationsPageServiceLimits.MaxPageSize, page.Items.Count, "Page size is capped.");
        Assert.IsTrue(page.Items.Zip(page.Items.Skip(1)).All(p => p.First.CreatedUtc >= p.Second.CreatedUtc), "Newest first.");
        Assert.DoesNotContain("secret-looking", JsonSerializer.Serialize(page), "Payload bodies are not exposed in history.");

        var failedOnly = (await client.GetFromJsonAsync<NotificationDeliveryPageResponse>($"{Root}/deliveries?state=Failed&pageSize=100", options))!;
        Assert.IsTrue(failedOnly.Items.All(i => i.State == "Failed" && i.CanRetry && !i.RetryNeedsAcknowledgement));
        var uncertain = (await client.GetFromJsonAsync<NotificationDeliveryPageResponse>($"{Root}/deliveries?state=Uncertain&pageSize=1", options))!.Items.Single();
        Assert.IsTrue(uncertain.RetryNeedsAcknowledgement);

        using var badState = await client.GetAsync($"{Root}/deliveries?state=Bogus");
        Assert.AreEqual(HttpStatusCode.BadRequest, badState.StatusCode);
        using var badKey = await client.GetAsync($"{Root}/deliveries?key=nope");
        Assert.AreEqual(HttpStatusCode.BadRequest, badKey.StatusCode);

        using var noAck = await client.SendAsync(Mutation(HttpMethod.Post, $"{Root}/deliveries/{uncertain.Id}/retry", new NotificationRetryRequest(uncertain.Revision)));
        Assert.AreEqual(HttpStatusCode.BadRequest, noAck.StatusCode, "An uncertain retry needs the duplicate-risk acknowledgement.");
        using var ack = await client.SendAsync(Mutation(HttpMethod.Post, $"{Root}/deliveries/{uncertain.Id}/retry", new NotificationRetryRequest(uncertain.Revision, true)));
        Assert.AreEqual(HttpStatusCode.OK, ack.StatusCode);
        using var staleRetry = await client.SendAsync(Mutation(HttpMethod.Post, $"{Root}/deliveries/{uncertain.Id}/retry", new NotificationRetryRequest(uncertain.Revision, true)));
        Assert.AreEqual(HttpStatusCode.Conflict, staleRetry.StatusCode);
        using var missing = await client.SendAsync(Mutation(HttpMethod.Post, $"{Root}/deliveries/nope/dismiss", new NotificationRevisionRequest(1)));
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        var failed = failedOnly.Items.First();
        using var dismissed = await client.SendAsync(Mutation(HttpMethod.Post, $"{Root}/deliveries/{failed.Id}/dismiss", new NotificationRevisionRequest(failed.Revision)));
        Assert.AreEqual(HttpStatusCode.OK, dismissed.StatusCode);
    }

    [TestMethod]
    public async Task SettingsPage_ShowsTheNotificationsSectionOnDemand_ForItsQueryValue_AndOnlyThen()
    {
        using var env = OpenInstance();
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var revision = (await OverviewAsync(client)).Destinations.Single(d => d.Kind == "smtp").ConfigRevision;
        using var saved = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/destinations/smtp", new NotificationDestinationRequest(revision,
            new SmtpDestinationRequest("smtp.example.org", 587, "starttls", "password", "mailer", "Hidden-Pw-123", false, "m3undle@example.org", null, ["a@example.org"]), null)));
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);

        var notifications = await client.GetStringAsync("/settings?section=notifications");
        StringAssert.Contains(notifications, "What to send, and how");
        StringAssert.Contains(notifications, "EPG source fetch failing");
        StringAssert.Contains(notifications, "Series synchronization completed", "The whole catalog is listed from the start.");
        StringAssert.Contains(notifications, "Send by");
        Assert.DoesNotContain("Hidden-Pw-123", notifications, "Secrets never reach the rendered page.");
        Assert.DoesNotContain("m3e:v2", notifications);

        var schedule = await client.GetStringAsync("/settings?section=schedule");
        Assert.DoesNotContain("What to send, and how", schedule, "Other sections do not load notification data.");
        var unknown = await client.GetStringAsync("/settings?section=bogus");
        Assert.DoesNotContain("What to send, and how", unknown);
    }

    [TestMethod]
    public async Task EveryNotificationRoute_FollowsTheUiAccessPolicy_AndCompatibilityRoutesStayAnonymous()
    {
        using var env = OpenInstance();
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _ = await OverviewAsync(client);

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(Root, StringComparison.Ordinal) == true)
            .ToList();
        Assert.IsGreaterThanOrEqualTo(9, endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            var policies = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(a => a.Policy).ToList();
            CollectionAssert.Contains(policies, "UiAccess", $"{endpoint.RoutePattern.RawText} must apply the UiAccess policy.");
        }

        using var health = await client.GetAsync("/health");
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, health.StatusCode);
    }

    [TestMethod]
    public async Task WhenUiAuthenticationIsEnabled_AnonymousCallersAreRefusedForReadsAndWrites()
    {
        using var env = new EnvScope(
            ("M3UNDLE_ENCRYPTION_KEY", Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            ("M3UNDLE_ENCRYPTION_KEYS", null),
            ("M3UNDLE_AUTH_ENABLED", "true"),
            ("M3UNDLE_ADMIN_USER", "admin@example.com"),
            ("M3UNDLE_ADMIN_PASSWORD", "Correct-Horse-Battery-9!"));
        await using var factory = new NotificationApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var read = await client.GetAsync(Root);
        Assert.IsTrue(read.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Forbidden, $"Got {read.StatusCode}");

        using var write = await client.SendAsync(Mutation(HttpMethod.Put, $"{Root}/settings", new NotificationRevisionRequest(1)));
        Assert.IsTrue(write.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Forbidden, $"Got {write.StatusCode}");

        using var health = await client.GetAsync("/health");
        Assert.AreNotEqual(HttpStatusCode.Unauthorized, health.StatusCode, "Anonymous compatibility routes are untouched.");
    }

    private static class NotificationsPageServiceLimits
    {
        public const int MaxPageSize = M3Undle.Web.Application.Notifications.NotificationsPageService.MaxPageSize;
    }

    private sealed class NotificationApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-notif-api-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDataDir);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["M3Undle:Paths:DataDirectory"] = _tempDataDir }));

            builder.ConfigureTestServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (descriptor is not null)
                    services.Remove(descriptor);

                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite(WebApplicationFactoryTestCleanup.CreateSqliteConnectionString(_tempDataDir))
                           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await WebApplicationFactoryTestCleanup.DeleteDirectoryWhenUnlockedAsync(_tempDataDir);
        }
    }
}
