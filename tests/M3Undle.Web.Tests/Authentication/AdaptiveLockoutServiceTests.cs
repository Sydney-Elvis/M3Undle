using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using M3Undle.Web.Application;
using M3Undle.Web.Data;

namespace M3Undle.Web.Tests.Authentication;

[TestClass]
public sealed class AdaptiveLockoutServiceTests
{
    [TestMethod]
    public async Task RegisterFailedPasswordAttemptAsync_AppliesShortLockout_OnFifthFailure()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            Email = "admin",
            NormalizedEmail = "ADMIN",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            LockoutEnabled = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var timeProvider = new StubTimeProvider(new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero));
        var service = new AdaptiveLockoutService(
            db,
            timeProvider,
            Options.Create(new AdaptiveLockoutOptions()));

        for (var i = 0; i < 4; i++)
        {
            var result = await service.RegisterFailedPasswordAttemptAsync(user, CancellationToken.None);
            Assert.IsFalse(result.LockoutApplied);
        }

        var lockout = await service.RegisterFailedPasswordAttemptAsync(user, CancellationToken.None);

        Assert.IsTrue(lockout.LockoutApplied);
        Assert.AreEqual(TimeSpan.FromSeconds(30), lockout.LockoutDuration);
        Assert.IsTrue(lockout.Escalated);
        Assert.AreEqual(0, user.AccessFailedCount);
        Assert.AreEqual(timeProvider.GetUtcNow().AddSeconds(30), user.LockoutEnd);
    }

    [TestMethod]
    public async Task RegisterFailedPasswordAttemptAsync_AppliesEscalatedLockout_AfterThreeMoreFailures()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            Email = "admin",
            NormalizedEmail = "ADMIN",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            LockoutEnabled = true,
            AdaptiveLockoutEscalated = true,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var timeProvider = new StubTimeProvider(new DateTimeOffset(2026, 4, 17, 12, 5, 0, TimeSpan.Zero));
        var service = new AdaptiveLockoutService(
            db,
            timeProvider,
            Options.Create(new AdaptiveLockoutOptions()));

        await service.RegisterFailedPasswordAttemptAsync(user, CancellationToken.None);
        await service.RegisterFailedPasswordAttemptAsync(user, CancellationToken.None);
        var lockout = await service.RegisterFailedPasswordAttemptAsync(user, CancellationToken.None);

        Assert.IsTrue(lockout.LockoutApplied);
        Assert.AreEqual(TimeSpan.FromMinutes(5), lockout.LockoutDuration);
        Assert.IsTrue(lockout.Escalated);
        Assert.AreEqual(0, user.AccessFailedCount);
        Assert.AreEqual(timeProvider.GetUtcNow().AddMinutes(5), user.LockoutEnd);
    }

    [TestMethod]
    public async Task ResetAsync_ClearsAdaptiveLockoutState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            Email = "admin",
            NormalizedEmail = "ADMIN",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            LockoutEnabled = true,
            AccessFailedCount = 2,
            AdaptiveLockoutEscalated = true,
            LockoutEnd = new DateTimeOffset(2026, 4, 17, 12, 10, 0, TimeSpan.Zero),
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var service = new AdaptiveLockoutService(
            db,
            new StubTimeProvider(new DateTimeOffset(2026, 4, 17, 12, 0, 0, TimeSpan.Zero)),
            Options.Create(new AdaptiveLockoutOptions()));

        await service.ResetAsync(user, CancellationToken.None);

        Assert.AreEqual(0, user.AccessFailedCount);
        Assert.IsFalse(user.AdaptiveLockoutEscalated);
        Assert.IsNull(user.LockoutEnd);
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
