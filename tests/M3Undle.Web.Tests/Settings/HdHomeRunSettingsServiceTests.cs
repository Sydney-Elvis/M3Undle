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
    public async Task GetSettingsAsync_ReturnsDefaultTunerCountWhenNoOverrideSet()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory);
        // TunerCount option is 4 — no override in DB
        var tunerResolver = new HdHomeRunTunerCountResolver(Options.Create(new HdHomeRunOptions { TunerCount = 4 }), fixture.ScopeFactory);

        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);
        var state = await service.GetSettingsAsync();

        Assert.IsNull(state.Saved.TunerCountOverride, "No override should be saved by default.");
        Assert.AreEqual(4, state.Saved.EffectiveTunerCount, "Effective count should equal the configured TunerCount when no override is set.");
    }

    [TestMethod]
    public async Task UpdateAsync_SetsTunerCountOverride_AndReadsBackCorrectly()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory);
        var tunerResolver = new HdHomeRunTunerCountResolver(Options.Create(new HdHomeRunOptions { TunerCount = 4 }), fixture.ScopeFactory);

        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);

        var result = await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true,
            TunerCountOverride: 2,
            FriendlyName: null,
            AdvertisedBaseUrl: null,
            DiscoveryEnabled: true,
            SsdpEnabled: false,
            SiliconDustDiscoveryEnabled: false));

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Error);
        Assert.AreEqual(2, result.Settings.TunerCountOverride);
        Assert.IsTrue(result.Changed);

        // Read back to verify persistence
        var state = await service.GetSettingsAsync();
        Assert.AreEqual(2, state.Saved.TunerCountOverride);
    }

    [TestMethod]
    public async Task UpdateAsync_RejectsInvalidTunerCountOverride()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory);
        var tunerResolver = new HdHomeRunTunerCountResolver(Options.Create(new HdHomeRunOptions { TunerCount = 4 }), fixture.ScopeFactory);

        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);

        var tooLow = await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true, TunerCountOverride: 0, FriendlyName: null,
            AdvertisedBaseUrl: null, DiscoveryEnabled: true,
            SsdpEnabled: false, SiliconDustDiscoveryEnabled: false));

        Assert.IsFalse(tooLow.Succeeded, "Override of 0 should be rejected.");

        var tooHigh = await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true, TunerCountOverride: 33, FriendlyName: null,
            AdvertisedBaseUrl: null, DiscoveryEnabled: true,
            SsdpEnabled: false, SiliconDustDiscoveryEnabled: false));

        Assert.IsFalse(tooHigh.Succeeded, "Override of 33 should be rejected.");
    }

    [TestMethod]
    public async Task UpdateAsync_ClearsTunerCountOverride_WhenSetToNull()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory);
        var tunerResolver = new HdHomeRunTunerCountResolver(Options.Create(new HdHomeRunOptions { TunerCount = 4 }), fixture.ScopeFactory);

        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);

        // Set an override first
        await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true, TunerCountOverride: 2, FriendlyName: null,
            AdvertisedBaseUrl: null, DiscoveryEnabled: true,
            SsdpEnabled: false, SiliconDustDiscoveryEnabled: false));

        // Clear it
        var result = await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true, TunerCountOverride: null, FriendlyName: null,
            AdvertisedBaseUrl: null, DiscoveryEnabled: true,
            SsdpEnabled: false, SiliconDustDiscoveryEnabled: false));

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Settings.TunerCountOverride, "Override should be cleared.");
    }

    [TestMethod]
    public async Task UpdateAsync_SetsFriendlyName_AndReadsBackCorrectly()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory);
        var tunerResolver = new HdHomeRunTunerCountResolver(Options.Create(new HdHomeRunOptions { TunerCount = 4 }), fixture.ScopeFactory);
        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);

        var result = await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true,
            TunerCountOverride: null,
            FriendlyName: "My IPTV Box",
            AdvertisedBaseUrl: null,
            DiscoveryEnabled: true,
            SsdpEnabled: true,
            SiliconDustDiscoveryEnabled: true));

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("My IPTV Box", result.Settings.FriendlyName);
        Assert.AreEqual("My IPTV Box", result.Settings.ResolvedFriendlyName);

        var state = await service.GetSettingsAsync();
        Assert.AreEqual("My IPTV Box", state.Saved.FriendlyName);
    }

    [TestMethod]
    public async Task UpdateAsync_ClearsFriendlyName_WhenSetToNull()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var options = Options.Create(new HdHomeRunOptions { TunerCount = 4, FriendlyName = "Configured Default" });
        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory, options);
        var tunerResolver = new HdHomeRunTunerCountResolver(options, fixture.ScopeFactory);
        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);

        await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true, TunerCountOverride: null, FriendlyName: "Override Name",
            AdvertisedBaseUrl: null, DiscoveryEnabled: true,
            SsdpEnabled: true, SiliconDustDiscoveryEnabled: true));

        var result = await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true, TunerCountOverride: null, FriendlyName: null,
            AdvertisedBaseUrl: null, DiscoveryEnabled: true,
            SsdpEnabled: true, SiliconDustDiscoveryEnabled: true));

        Assert.IsTrue(result.Succeeded);
        Assert.IsNull(result.Settings.FriendlyName, "DB override should be cleared.");
        Assert.AreEqual("Configured Default", result.Settings.ResolvedFriendlyName, "Should fall back to config option.");
    }

    [TestMethod]
    public async Task UpdateAsync_RejectsFriendlyNameExceeding128Characters()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory);
        var tunerResolver = new HdHomeRunTunerCountResolver(Options.Create(new HdHomeRunOptions { TunerCount = 4 }), fixture.ScopeFactory);
        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);

        var result = await service.UpdateAsync(new UpdateHdhrSettingsCommand(
            Enabled: true, TunerCountOverride: null,
            FriendlyName: new string('A', 129),
            AdvertisedBaseUrl: null, DiscoveryEnabled: true,
            SsdpEnabled: true, SiliconDustDiscoveryEnabled: true));

        Assert.IsFalse(result.Succeeded, "FriendlyName over 128 characters should be rejected.");
        Assert.IsNotNull(result.Error);
    }

    [TestMethod]
    public async Task GetSettingsAsync_ResolvedFriendlyName_FallsBackToConfiguredOption()
    {
        Environment.SetEnvironmentVariable("M3UNDLE_HDHR_ENABLED", null);

        await using var fixture = await CreateFixtureAsync();
        await using var db = fixture.CreateDbContext();

        var options = Options.Create(new HdHomeRunOptions { TunerCount = 4, FriendlyName = "Custom Config Name" });
        var deviceService = CreateDeviceService(fixture.ScopeFactory, fixture.TempDataDirectory, options);
        var tunerResolver = new HdHomeRunTunerCountResolver(options, fixture.ScopeFactory);
        var service = new HdHomeRunSettingsService(db, deviceService, tunerResolver);

        var state = await service.GetSettingsAsync();

        Assert.IsNull(state.Saved.FriendlyName, "No DB override set.");
        Assert.AreEqual("Custom Config Name", state.Saved.ResolvedFriendlyName, "Should fall back to configured option.");
    }

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

    private static HdHomeRunDeviceService CreateDeviceService(
        IServiceScopeFactory scopeFactory,
        string tempDataDirectory,
        IOptions<HdHomeRunOptions>? options = null)
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

        options ??= Options.Create(new HdHomeRunOptions
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
