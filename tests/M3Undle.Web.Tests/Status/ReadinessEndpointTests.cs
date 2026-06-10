using System.Net;
using System.Text.Json;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Status;

[TestClass]
public sealed class ReadinessEndpointTests
{
    [TestMethod]
    public async Task HealthReady_WhenNoActiveProfile_Returns503WithNoActiveProfileReason()
    {
        await using var factory = new ReadinessApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("no active profile", json.RootElement.GetProperty("reason").GetString());

        var reasons = json.RootElement.GetProperty("reasons").EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        CollectionAssert.Contains(reasons, "no active profile");
    }

    [TestMethod]
    public async Task M3UndleReadyHealthData_ReasonsToString_ReturnsReadableReasons()
    {
        await using var factory = new ReadinessApiFactory();
        var healthChecks = factory.Services.GetRequiredService<HealthCheckService>();

        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Name == "m3undle_ready");

        var data = report.Entries["m3undle_ready"].Data;
        Assert.AreEqual("no active profile", data["reasons"].ToString());
    }

    [TestMethod]
    public async Task HealthReady_WhenSnapshotExistsOnlyForInactiveProfile_Returns503ForActiveProfileSnapshot()
    {
        await using var factory = new ReadinessApiFactory();
        await factory.SeedAsync(db =>
        {
            var now = DateTime.UtcNow;
            db.Profiles.AddRange(
                MakeProfile("profile-active", isActive: true, enabled: true, now),
                MakeProfile("profile-other", isActive: false, enabled: true, now));
            db.Snapshots.Add(new Snapshot
            {
                SnapshotId = "snapshot-other",
                ProfileId = "profile-other",
                CreatedUtc = now,
                Status = "active",
                PlaylistPath = "playlist.m3u",
                XmltvPath = "guide.xml",
                ChannelIndexPath = "channel_index.ndjson",
                StatusJsonPath = "status.json",
                ChannelCountPublished = 12,
                LiveChannelCount = 12,
            });
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("no active snapshot for active profile", json.RootElement.GetProperty("reason").GetString());

        var reasons = json.RootElement.GetProperty("reasons").EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        CollectionAssert.Contains(reasons, "no active snapshot for active profile");
    }

    [TestMethod]
    public async Task HealthReady_WhenActiveProfileHasSnapshot_ReturnsReadyTrue()
    {
        await using var factory = new ReadinessApiFactory();
        await factory.SeedAsync(db =>
        {
            var now = DateTime.UtcNow;
            db.Profiles.Add(MakeProfile("profile-active", isActive: true, enabled: true, now));
            db.Snapshots.Add(new Snapshot
            {
                SnapshotId = "snapshot-active",
                ProfileId = "profile-active",
                CreatedUtc = now,
                Status = "active",
                PlaylistPath = "playlist.m3u",
                XmltvPath = "guide.xml",
                ChannelIndexPath = "channel_index.ndjson",
                StatusJsonPath = "status.json",
                ChannelCountPublished = 22,
                LiveChannelCount = 22,
            });
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsTrue(json.RootElement.GetProperty("ready").GetBoolean());
    }

    [TestMethod]
    public async Task HealthReady_WhenRefreshInProgressAndActiveSnapshotExists_ReturnsReadyTrue()
    {
        await using var factory = new ReadinessApiFactory(isRefreshing: true);
        await factory.SeedAsync(db =>
        {
            var now = DateTime.UtcNow;
            db.Profiles.Add(MakeProfile("profile-active", isActive: true, enabled: true, now));
            db.Snapshots.Add(new Snapshot
            {
                SnapshotId = "snapshot-active",
                ProfileId = "profile-active",
                CreatedUtc = now,
                Status = "active",
                PlaylistPath = "playlist.m3u",
                XmltvPath = "guide.xml",
                ChannelIndexPath = "channel_index.ndjson",
                StatusJsonPath = "status.json",
                ChannelCountPublished = 22,
                LiveChannelCount = 22,
            });
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsTrue(json.RootElement.GetProperty("ready").GetBoolean());
    }

    [TestMethod]
    public async Task HealthReady_WhenRefreshInProgressAndNoActiveSnapshot_Returns503WithSnapshotReason()
    {
        await using var factory = new ReadinessApiFactory(isRefreshing: true);
        await factory.SeedAsync(db =>
        {
            var now = DateTime.UtcNow;
            db.Profiles.Add(MakeProfile("profile-active", isActive: true, enabled: true, now));
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var reasons = json.RootElement.GetProperty("reasons").EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        CollectionAssert.Contains(reasons, "no active snapshot for active profile");
        CollectionAssert.Contains(reasons, "refresh in progress");
    }

    private static Profile MakeProfile(string profileId, bool isActive, bool enabled, DateTime now) => new()
    {
        ProfileId = profileId,
        Name = profileId,
        Enabled = enabled,
        IsActive = isActive,
        OutputName = "m3undle",
        MergeMode = "replace",
        CreatedUtc = now,
        UpdatedUtc = now,
    };

    private sealed class ReadinessApiFactory(bool isRefreshing = false) : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-readiness-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDataDir);

            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["M3Undle:Paths:DataDirectory"] = _tempDataDir,
                });
            });

            builder.ConfigureTestServices(services =>
            {
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (dbDescriptor is not null)
                    services.Remove(dbDescriptor);

                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite(WebApplicationFactoryTestCleanup.CreateSqliteConnectionString(_tempDataDir))
                           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IRefreshTrigger)).ToList())
                    services.Remove(descriptor);

                services.AddSingleton<IRefreshTrigger>(new TestRefreshTrigger(isRefreshing));
            });
        }

        public async Task SeedAsync(Action<ApplicationDbContext> seed)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            seed(db);
            await db.SaveChangesAsync();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await WebApplicationFactoryTestCleanup.DeleteDirectoryWhenUnlockedAsync(_tempDataDir);
        }
    }

    private sealed class TestRefreshTrigger(bool isRefreshing) : IRefreshTrigger
    {
        public bool IsRefreshing { get; } = isRefreshing;
        public DateTime? RefreshStartedAt => null;
        public string? CurrentActivity => null;

        public bool TriggerRefresh() => false;

        public bool TriggerBuildOnly() => false;

        public void CancelRefresh()
        {
        }
    }
}
