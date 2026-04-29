using System.Net;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Observability;
using M3Undle.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Observability;

[TestClass]
public sealed class ObservabilityTests
{
    [TestMethod]
    public async Task MetricsTokenService_CreateAuthenticateAndDelete_Works()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var service = new MetricsTokenService(db, TimeProvider.System);
        var created = await service.CreateAsync(new CreateMetricsTokenCommand("Prometheus", null), CancellationToken.None);

        Assert.IsTrue(created.Succeeded, created.Error);
        Assert.IsNotNull(created.PlainToken);
        Assert.AreNotEqual(created.PlainToken, (await db.MetricsTokens.SingleAsync()).TokenHash);

        Assert.IsTrue(await service.TryAuthenticateAsync(created.PlainToken, CancellationToken.None));
        var stored = await db.MetricsTokens.SingleAsync();
        Assert.IsNotNull(stored.LastUsedUtc);

        var tokens = await service.ListAsync(CancellationToken.None);
        Assert.HasCount(1, tokens);
        Assert.AreEqual("metrics:read", tokens[0].Scope);

        Assert.IsTrue(await service.DeleteAsync(tokens[0].MetricsTokenId, CancellationToken.None));
        Assert.IsFalse(await service.TryAuthenticateAsync(created.PlainToken, CancellationToken.None));
    }

    [TestMethod]
    public async Task ObservabilitySettingsService_CachesSettingsAndRefreshesOnUpdate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new ObservabilitySettingsService(db, cache, Options.Create(new ObservabilityOptions()));

        var initial = await service.GetSettingsAsync(CancellationToken.None);
        Assert.AreEqual(MetricsAccessModes.LocalOnly, initial.Mode);

        var settings = await db.SiteSettings.FirstAsync();
        settings.ObservabilityMetricsMode = MetricsAccessModes.Disabled;
        settings.ObservabilityMetricsEnabled = false;
        await db.SaveChangesAsync();

        var cached = await service.GetSettingsAsync(CancellationToken.None);
        Assert.AreEqual(MetricsAccessModes.LocalOnly, cached.Mode);

        var updated = await service.UpdateAsync(
            new UpdateObservabilitySettingsCommand(true, MetricsAccessModes.Token, false, []),
            CancellationToken.None);
        Assert.IsTrue(updated.Succeeded, updated.Error);

        var refreshed = await service.GetSettingsAsync(CancellationToken.None);
        Assert.AreEqual(MetricsAccessModes.Token, refreshed.Mode);
    }

    [TestMethod]
    public async Task MetricsEndpoint_DefaultLocalOnly_AllowsTestServerScrape()
    {
        await using var factory = new ObservabilityApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/metrics");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        StringAssert.Contains(body, "m3undle");
    }

    [TestMethod]
    public async Task MetricsEndpoint_Disabled_ReturnsNotFound()
    {
        await using var factory = new ObservabilityApiFactory();
        await factory.UpdateSettingsAsync(settings =>
        {
            settings.ObservabilityMetricsEnabled = false;
            settings.ObservabilityMetricsMode = MetricsAccessModes.Disabled;
        });

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/metrics");
        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task MetricsEndpoint_TokenMode_RequiresBearerToken()
    {
        await using var factory = new ObservabilityApiFactory();
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.SiteSettings.FirstAsync();
            settings.ObservabilityMetricsEnabled = true;
            settings.ObservabilityMetricsMode = MetricsAccessModes.Token;
            await db.SaveChangesAsync();

            var tokenService = scope.ServiceProvider.GetRequiredService<IMetricsTokenService>();
            var created = await tokenService.CreateAsync(new CreateMetricsTokenCommand("Prometheus", null), CancellationToken.None);
            Assert.IsTrue(created.Succeeded, created.Error);
            token = created.PlainToken!;
        }

        using var client = factory.CreateClient();
        using (var unauthorized = await client.GetAsync("/metrics"))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        using var authorized = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, authorized.StatusCode);
    }

    [TestMethod]
    public async Task MetricsEndpoint_RateLimitsExcessiveScrapes()
    {
        await using var factory = new ObservabilityApiFactory();
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 13; i++)
        {
            using var response = await client.GetAsync("/metrics");
            statuses.Add(response.StatusCode);
        }

        Assert.IsTrue(statuses.Take(12).All(x => x == HttpStatusCode.OK));
        Assert.AreEqual((HttpStatusCode)429, statuses[12]);
    }

    private sealed class ObservabilityApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-observability-{Guid.NewGuid():N}");

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
            });
        }

        public async Task UpdateSettingsAsync(Action<SiteSettings> update)
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var settings = await db.SiteSettings.FirstAsync();
            update(settings);
            await db.SaveChangesAsync();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await WebApplicationFactoryTestCleanup.DeleteDirectoryWhenUnlockedAsync(_tempDataDir);
        }
    }
}
