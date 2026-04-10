using M3Undle.Web.Application;
using M3Undle.Web.Contracts.Providers;
using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

[TestClass]
public sealed class ProviderPageServiceValidationTests
{
    [TestMethod]
    public async Task CreateProviderAsync_WhenPlaylistAndXtreamInputsMixed_ReturnsValidationError()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Services);

        var request = new CreateProviderRequest
        {
            Name = "Mixed Provider",
            PlaylistUrl = "https://example.test/playlist.m3u8",
            XtreamBaseUrl = "https://panel.example.test",
            XtreamUsername = "user",
            XtreamPassword = "pass",
            TimeoutSeconds = 120,
        };

        var (provider, error) = await service.CreateProviderAsync(request, CancellationToken.None);

        Assert.IsNull(provider);
        Assert.AreEqual("Playlist/file fields and Xtream fields are mutually exclusive.", error);

        await using var verify = fixture.CreateDbContext();
        Assert.AreEqual(0, await verify.Providers.CountAsync());
    }

    [TestMethod]
    public async Task CreateProviderAsync_WhenXtreamFieldsProvidedWithoutBaseUrl_ReturnsValidationError()
    {
        await using var fixture = await CreateFixtureAsync();
        var service = CreateService(fixture.Services);

        var request = new CreateProviderRequest
        {
            Name = "Invalid Xtream Provider",
            XtreamUsername = "user",
            XtreamPassword = "pass",
            XtreamIncludeXmltv = true,
            TimeoutSeconds = 120,
        };

        var (provider, error) = await service.CreateProviderAsync(request, CancellationToken.None);

        Assert.IsNull(provider);
        Assert.AreEqual("xtreamUsername/xtreamPassword/xtreamIncludeXmltv require xtreamBaseUrl.", error);

        await using var verify = fixture.CreateDbContext();
        Assert.AreEqual(0, await verify.Providers.CountAsync());
    }

    private static ProviderPageService CreateService(ServiceProvider services)
    {
        var envVars = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var encryption = new SecretEncryptionService(envVars);
        return new ProviderPageService(
            services.GetRequiredService<IServiceScopeFactory>(),
            fetcher: null!,
            configService: null!,
            envVarService: envVars,
            encryption,
            refreshTrigger: new TestRefreshTrigger(),
            eventBus: new AppEventBus(),
            logger: NullLogger<ProviderPageService>.Instance);
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));
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
        public SqliteConnection Connection { get; } = connection;
        public ServiceProvider Services { get; } = services;

        public ApplicationDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(Connection)
                .Options;
            return new ApplicationDbContext(options);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestRefreshTrigger : IRefreshTrigger
    {
        public bool IsRefreshing => false;
        public bool TriggerRefresh() => true;
        public bool TriggerBuildOnly() => true;
        public void CancelRefresh() { }
    }
}
