using M3Undle.Web.Application.Epg;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Epg;

/// <summary>
/// Checklist: Channel mappings persist and affect the published guide.
/// Saves EpgChannelMapping rows to SQLite, reads them back via DB query, and
/// verifies EpgCompiler output includes only the mapped programmes.
/// </summary>
[TestClass]
public sealed class EpgMappingPersistenceTests
{
    private readonly EpgCompiler _compiler = new(NullLogger<EpgCompiler>.Instance);

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [TestMethod]
    public async Task PersistedMapping_AffectsCompiledGuide()
    {
        await using var fixture = await DbFixture.CreateAsync();

        const string providerChannelId = "pc-cnn";
        const string epgSourceId = "epg-src-1";
        const string xmltvChannelId = "cnn.us";

        await fixture.EnsureProviderChannelAsync(providerChannelId);
        await fixture.SeedEpgSourceAsync(epgSourceId);
        await fixture.SeedAsync(async db =>
        {
            db.EpgChannelMappings.Add(new EpgChannelMapping
            {
                EpgChannelMappingId = Guid.NewGuid().ToString(),
                ProfileId = fixture.ProfileId,
                ProviderChannelId = providerChannelId,
                EpgSourceId = epgSourceId,
                XmltvChannelId = xmltvChannelId,
                MappingMode = "manual",
                Confidence = 1.0f,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        var (mappings, sources) = await fixture.LoadMappingsAndSourcesAsync();

        var now = DateTimeOffset.UtcNow;
        var catalogue = new EpgCatalogue(
            epgSourceId,
            [new EpgChannelRecord(epgSourceId, xmltvChannelId, "CNN", null)],
            new Dictionary<string, IReadOnlyList<EpgProgrammeRecord>>
            {
                [xmltvChannelId] = [new EpgProgrammeRecord(epgSourceId, xmltvChannelId, now, now.AddHours(1), "Persisted Show", null, null, [], [], null)]
            });

        var outputChannels = new List<OutputChannel>
        {
            new(providerChannelId, xmltvChannelId, "CNN", null)
        };

        var (xml, report) = _compiler.Compile(
            outputChannels,
            sources,
            new Dictionary<string, EpgCatalogue> { [epgSourceId] = catalogue },
            mappings);

        Assert.AreEqual(1, report.MappedChannels, "One channel should be mapped via the persisted mapping.");
        Assert.AreEqual(1, report.TotalProgrammes, "The mapped programme should appear in the output.");
        StringAssert.Contains(xml, "Persisted Show");
        StringAssert.Contains(xml, $"channel=\"{xmltvChannelId}\"");
    }

    [TestMethod]
    public async Task NoPersistedMappings_ProducesNoProgrammesInOutput()
    {
        await using var fixture = await DbFixture.CreateAsync();
        const string epgSourceId = "epg-src-empty";
        await fixture.SeedEpgSourceAsync(epgSourceId);

        var (mappings, sources) = await fixture.LoadMappingsAndSourcesAsync();

        var now = DateTimeOffset.UtcNow;
        var catalogue = new EpgCatalogue(
            epgSourceId,
            [new EpgChannelRecord(epgSourceId, "cnn.us", "CNN", null)],
            new Dictionary<string, IReadOnlyList<EpgProgrammeRecord>>
            {
                ["cnn.us"] = [new EpgProgrammeRecord(epgSourceId, "cnn.us", now, now.AddHours(1), "Orphaned Show", null, null, [], [], null)]
            });

        var outputChannels = new List<OutputChannel>
        {
            new("pc-cnn", "cnn.us", "CNN", null)
        };

        var (xml, report) = _compiler.Compile(
            outputChannels,
            sources,
            new Dictionary<string, EpgCatalogue> { [epgSourceId] = catalogue },
            mappings);

        Assert.AreEqual(0, report.MappedChannels, "No mappings in DB → zero mapped channels.");
        Assert.AreEqual(0, report.TotalProgrammes, "No mappings → no programmes in output.");
        Assert.DoesNotContain("Orphaned Show", xml);
    }

    [TestMethod]
    public async Task UpdatedMapping_ReplacedXmltvChannelId_AffectsOutput()
    {
        // Verifies that updating a mapping in the DB changes guide output on the next compile.
        await using var fixture = await DbFixture.CreateAsync();

        const string providerChannelId = "pc-sports";
        const string epgSourceId = "epg-src-sports";
        const string mappingId = "map-sports-1";

        await fixture.EnsureProviderChannelAsync(providerChannelId);
        await fixture.SeedEpgSourceAsync(epgSourceId);

        // Insert initial mapping pointing at espn.us
        await fixture.SeedAsync(async db =>
        {
            db.EpgChannelMappings.Add(new EpgChannelMapping
            {
                EpgChannelMappingId = mappingId,
                ProfileId = fixture.ProfileId,
                ProviderChannelId = providerChannelId,
                EpgSourceId = epgSourceId,
                XmltvChannelId = "espn.us",
                MappingMode = "manual",
                Confidence = 1.0f,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        });

        // Update the mapping to point at espn2.us
        await fixture.SeedAsync(async db =>
        {
            var mapping = await db.EpgChannelMappings.FindAsync(mappingId);
            mapping!.XmltvChannelId = "espn2.us";
            await db.SaveChangesAsync();
        });

        var (mappings, sources) = await fixture.LoadMappingsAndSourcesAsync();

        var now = DateTimeOffset.UtcNow;
        var catalogue = new EpgCatalogue(
            epgSourceId,
            [
                new EpgChannelRecord(epgSourceId, "espn.us",  "ESPN",  null),
                new EpgChannelRecord(epgSourceId, "espn2.us", "ESPN2", null),
            ],
            new Dictionary<string, IReadOnlyList<EpgProgrammeRecord>>
            {
                ["espn.us"]  = [new EpgProgrammeRecord(epgSourceId, "espn.us",  now, now.AddHours(1), "ESPN Show",  null, null, [], [], null)],
                ["espn2.us"] = [new EpgProgrammeRecord(epgSourceId, "espn2.us", now, now.AddHours(1), "ESPN2 Show", null, null, [], [], null)],
            });

        var outputChannels = new List<OutputChannel>
        {
            new(providerChannelId, "espn2.us", "Sports Channel", null)
        };

        var (xml, report) = _compiler.Compile(
            outputChannels,
            sources,
            new Dictionary<string, EpgCatalogue> { [epgSourceId] = catalogue },
            mappings);

        Assert.AreEqual(1, report.TotalProgrammes);
        StringAssert.Contains(xml, "ESPN2 Show");
        Assert.DoesNotContain("ESPN Show", xml);
    }

    // -------------------------------------------------------------------------
    // Fixture — shared DB setup boilerplate
    // -------------------------------------------------------------------------

    private sealed class DbFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<ApplicationDbContext> _options;

        public string ProfileId { get; } = "profile-1";
        public string ProviderId { get; } = "provider-1";
        public string ProviderChannelId { get; } = "pc-cnn";

        private DbFixture(SqliteConnection connection, DbContextOptions<ApplicationDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var setup = new ApplicationDbContext(options);
            await setup.Database.EnsureCreatedAsync();

            var fixture = new DbFixture(connection, options);

            // Seed the parent entities required by EpgChannelMapping FKs
            await using var db = new ApplicationDbContext(options);
            var now = DateTime.UtcNow;

            db.Profiles.Add(new Profile
            {
                ProfileId = fixture.ProfileId,
                Name = "Test Profile",
                Enabled = true,
                OutputName = "test",
                MergeMode = "union",
                CreatedUtc = now,
                UpdatedUtc = now,
            });

            db.Providers.Add(new Provider
            {
                ProviderId = fixture.ProviderId,
                Name = "Test Provider",
                Enabled = true,
                PlaylistUrl = "http://fake/playlist.m3u",
                TimeoutSeconds = 30,
                CreatedUtc = now,
                UpdatedUtc = now,
            });

            db.FetchRuns.Add(new FetchRun
            {
                FetchRunId = "run-1",
                ProviderId = fixture.ProviderId,
                StartedUtc = now,
                Status = "ok",
                Type = "snapshot",
            });

            // A ProviderChannel is required as the FK target for EpgChannelMapping.ProviderChannelId.
            // We add one for every test; individual tests may reference other IDs directly
            // (SQLite will enforce FKs, so each unique ProviderChannelId must exist).
            db.ProviderChannels.Add(new ProviderChannel
            {
                ProviderChannelId = fixture.ProviderChannelId,
                ProviderId = fixture.ProviderId,
                DisplayName = "CNN",
                StreamUrl = "http://fake/stream",
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Active = true,
                ContentType = "live",
                LastFetchRunId = "run-1",
            });

            await db.SaveChangesAsync();
            return fixture;
        }

        /// <summary>Adds a ProviderChannel row for a given ID (needed for FK references).</summary>
        public async Task EnsureProviderChannelAsync(string providerChannelId)
        {
            await using var db = new ApplicationDbContext(_options);
            if (!await db.ProviderChannels.AnyAsync(x => x.ProviderChannelId == providerChannelId))
            {
                db.ProviderChannels.Add(new ProviderChannel
                {
                    ProviderChannelId = providerChannelId,
                    ProviderId = ProviderId,
                    DisplayName = providerChannelId,
                    StreamUrl = "http://fake/stream",
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                    Active = true,
                    ContentType = "live",
                    LastFetchRunId = "run-1",
                });
                await db.SaveChangesAsync();
            }
        }

        public async Task SeedEpgSourceAsync(string epgSourceId)
        {
            await using var db = new ApplicationDbContext(_options);
            db.EpgSources.Add(new EpgSource
            {
                EpgSourceId = epgSourceId,
                Name = epgSourceId,
                Kind = "xmltv_url",
                Priority = 1,
                Enabled = true,
                TimeoutSeconds = 30,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedAsync(Func<ApplicationDbContext, Task> action)
        {
            await using var db = new ApplicationDbContext(_options);
            await action(db);
        }

        public async Task<(ILookup<string, EpgChannelMapping> Mappings, IReadOnlyList<EpgSource> Sources)>
            LoadMappingsAndSourcesAsync()
        {
            await using var db = new ApplicationDbContext(_options);
            var mappings = (await db.EpgChannelMappings.AsNoTracking().ToListAsync())
                .ToLookup(m => m.ProviderChannelId, StringComparer.Ordinal);
            var sources = await db.EpgSources.AsNoTracking().OrderBy(x => x.Priority).ToListAsync();
            return (mappings, sources);
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
        }
    }
}
