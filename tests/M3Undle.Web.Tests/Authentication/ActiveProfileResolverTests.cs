using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Authentication;

[TestClass]
public sealed class ActiveProfileResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_WhenPreferredProfileIsEnabled_ReturnsIt()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        db.Profiles.Add(MakeProfile("p1", "Alpha", enabled: true));
        await db.SaveChangesAsync();

        var result = await new ActiveProfileResolver(db).ResolveActiveProfileIdAsync("p1", CancellationToken.None);

        Assert.AreEqual("p1", result);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenPreferredProfileIsDisabled_ReturnsNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        db.Profiles.Add(MakeProfile("p1", "Alpha", enabled: false));
        db.Profiles.Add(MakeProfile("p2", "Beta", enabled: true));
        await db.SaveChangesAsync();

        var result = await new ActiveProfileResolver(db).ResolveActiveProfileIdAsync("p1", CancellationToken.None);

        // Must hard-fail rather than silently falling back to p2.
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenPreferredProfileDoesNotExist_ReturnsNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        db.Profiles.Add(MakeProfile("p1", "Alpha", enabled: true));
        await db.SaveChangesAsync();

        var result = await new ActiveProfileResolver(db).ResolveActiveProfileIdAsync("nonexistent", CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenNoPreferred_ReturnsActiveSnapshotProfile()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        db.Profiles.Add(MakeProfile("p1", "Alpha", enabled: true));
        await db.SaveChangesAsync();

        db.Snapshots.Add(new Snapshot
        {
            SnapshotId = "snap-1",
            ProfileId = "p1",
            Status = "active",
            CreatedUtc = DateTime.UtcNow,
            PlaylistPath = string.Empty,
            XmltvPath = string.Empty,
            ChannelIndexPath = string.Empty,
            StatusJsonPath = string.Empty,
        });
        await db.SaveChangesAsync();

        var result = await new ActiveProfileResolver(db).ResolveActiveProfileIdAsync(null, CancellationToken.None);

        Assert.AreEqual("p1", result);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenNoPreferredAndNoSnapshot_ReturnsFallbackEnabledProfileByName()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        db.Profiles.Add(MakeProfile("p-beta", "Beta", enabled: true));
        db.Profiles.Add(MakeProfile("p-alpha", "Alpha", enabled: true));
        await db.SaveChangesAsync();

        var result = await new ActiveProfileResolver(db).ResolveActiveProfileIdAsync(null, CancellationToken.None);

        Assert.AreEqual("p-alpha", result);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenNoPreferredAndNoProfilesAtAll_ReturnsNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();

        var result = await new ActiveProfileResolver(db).ResolveActiveProfileIdAsync(null, CancellationToken.None);

        Assert.IsNull(result);
    }

    private static ApplicationDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        return new ApplicationDbContext(options);
    }

    private static Profile MakeProfile(string id, string name, bool enabled) => new()
    {
        ProfileId = id,
        Name = name,
        Enabled = enabled,
        OutputName = name,
        MergeMode = "default",
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };
}
