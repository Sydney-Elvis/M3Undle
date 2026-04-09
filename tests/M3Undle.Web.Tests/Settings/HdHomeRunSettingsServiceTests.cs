using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Settings;

[TestClass]
public sealed class HdHomeRunSettingsServiceTests
{
    [TestMethod]
    public async Task GetSettingsAsync_AppliedSnapshot_UsesStartupLatchedRuntimeValues()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var initialSettings = await db.SiteSettings.OrderBy(x => x.Id).FirstAsync();
        initialSettings.HdhrEnabled = true;
        initialSettings.HdhrDiscoveryEnabled = true;
        initialSettings.HdhrSsdpEnabled = true;
        initialSettings.HdhrSiliconDustDiscoveryEnabled = true;
        initialSettings.HdhrTunerCountOverride = 2;
        await db.SaveChangesAsync();

        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory);
        var tunerResolver = new HdHomeRunTunerCountResolver(Options.Create(new HdHomeRunOptions { TunerCount = 4 }), fixture.ScopeFactory);

        deviceService.CaptureRuntimeSnapshot();

        var settings = await db.SiteSettings.OrderBy(x => x.Id).FirstAsync();
        settings.HdhrEnabled = false;
        settings.HdhrDiscoveryEnabled = false;
        settings.HdhrSsdpEnabled = false;
        settings.HdhrSiliconDustDiscoveryEnabled = false;
        settings.HdhrTunerCountOverride = 7;
        await db.SaveChangesAsync();

        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);
        var state = await service.GetSettingsAsync();

        Assert.IsFalse(state.Saved.Enabled);
        Assert.IsFalse(state.Saved.DiscoveryEnabled);
        Assert.IsFalse(state.Saved.SsdpEnabled);
        Assert.IsFalse(state.Saved.SiliconDustDiscoveryEnabled);

        Assert.IsTrue(state.Applied.Enabled);
        Assert.IsTrue(state.Applied.DiscoveryEnabled);
        Assert.IsTrue(state.Applied.SsdpEnabled);
        Assert.IsTrue(state.Applied.SiliconDustDiscoveryEnabled);
    }

    private static HdHomeRunDeviceService CreateDeviceService(IServiceScopeFactory scopeFactory, string tempDataDirectory)
    {
        var runtimePaths = new RuntimePaths(
            DataDirectory: tempDataDirectory,
            DatabasePath: Path.Combine(tempDataDirectory, "unused.db"),
            DatabaseConnectionString: "Data Source=:memory:",
            LogDirectory: Path.Combine(tempDataDirectory, "logs"),
            SnapshotDirectory: Path.Combine(tempDataDirectory, "snapshots"));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ASPNETCORE_URLS"] = "http://localhost:8080",
            })
            .Build();

        var options = Options.Create(new HdHomeRunOptions
        {
            Enabled = true,
            DiscoveryEnabled = true,
            SsdpEnabled = true,
            SiliconDustDiscoveryEnabled = true,
            TunerCount = 4,
        });

        var env = new EnvironmentVariableService(NullLogger<EnvironmentVariableService>.Instance);
        var tunerResolver = new HdHomeRunTunerCountResolver(options, scopeFactory);

        return new HdHomeRunDeviceService(
            runtimePaths,
            options,
            config,
            env,
            tunerResolver,
            scopeFactory,
            NullLogger<HdHomeRunDeviceService>.Instance);
    }

    private static async Task<TestFixture> CreateFixtureAsync()
    {
        var tempDataDirectory = Path.Combine(Path.GetTempPath(), $"m3undle-hdhr-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDataDirectory);

        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
        var provider = services.BuildServiceProvider();

        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        return new TestFixture(connection, options, provider, tempDataDirectory);
    }

    private sealed class TestFixture(
        SqliteConnection connection,
        DbContextOptions<ApplicationDbContext> options,
        ServiceProvider provider,
        string tempDataDirectory) : IAsyncDisposable
    {
        public IServiceScopeFactory ScopeFactory => provider.GetRequiredService<IServiceScopeFactory>();
        public string TempDataDirectory => tempDataDirectory;

        public ApplicationDbContext CreateDbContext() => new(options);

        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            await provider.DisposeAsync();
            if (Directory.Exists(tempDataDirectory))
                Directory.Delete(tempDataDirectory, recursive: true);
        }
    }
}
