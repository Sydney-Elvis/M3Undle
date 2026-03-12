using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Authentication;

[TestClass]
public sealed class EndpointSecurityServiceTests
{
    [TestMethod]
    public async Task UpdateAsync_EnableSecurity_CreatesCredentialAndBinding()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var service = new EndpointSecurityService(db);
        var result = await service.UpdateAsync(new UpdateEndpointSecurityCommand(
            Enabled: true,
            Username: "iptv-user",
            Password: "secret-pass",
            ActiveProfileId: null), CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Error);

        var settings = await service.GetSettingsAsync(CancellationToken.None);
        Assert.IsTrue(settings.Enabled);
        Assert.IsTrue(settings.HasCredential);
        Assert.AreEqual("iptv-user", settings.Username);

        var credential = await db.EndpointCredentials.AsNoTracking().SingleAsync();
        Assert.AreEqual("iptv-user", credential.Username);
        Assert.AreEqual("IPTV-USER", credential.NormalizedUsername);
        Assert.AreNotEqual("secret-pass", credential.PasswordHash);

        var binding = await db.EndpointAccessBindings.AsNoTracking().SingleAsync();
        Assert.AreEqual(credential.EndpointCredentialId, binding.EndpointCredentialId);
        Assert.AreEqual("hdhr-main", binding.VirtualTunerId);
        Assert.IsTrue(binding.Enabled);
    }

    [TestMethod]
    public async Task UpdateAsync_UpdateExistingCredential_ChangesUsernameAndPassword()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var service = new EndpointSecurityService(db);
        await service.UpdateAsync(new UpdateEndpointSecurityCommand(
            Enabled: true,
            Username: "original-user",
            Password: "original-pass",
            ActiveProfileId: null), CancellationToken.None);

        var result = await service.UpdateAsync(new UpdateEndpointSecurityCommand(
            Enabled: true,
            Username: "updated-user",
            Password: "updated-pass",
            ActiveProfileId: null), CancellationToken.None);

        Assert.IsTrue(result.Succeeded, result.Error);
        Assert.AreEqual("updated-user", result.Settings.Username);

        var credential = await db.EndpointCredentials.AsNoTracking().SingleAsync();
        Assert.AreEqual("updated-user", credential.Username);
        Assert.AreEqual("UPDATED-USER", credential.NormalizedUsername);
        Assert.AreNotEqual("updated-pass", credential.PasswordHash);
    }

    [TestMethod]
    public async Task UpdateAsync_WhenMultipleCredentialsExist_ReturnsFail()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var now = DateTime.UtcNow;
        db.EndpointCredentials.Add(new EndpointCredential
        {
            EndpointCredentialId = Guid.NewGuid().ToString(),
            Username = "user-a",
            NormalizedUsername = "USER-A",
            PasswordHash = "hash-a",
            Enabled = true,
            AuthType = "password",
            CreatedUtc = now,
            UpdatedUtc = now,
        });
        db.EndpointCredentials.Add(new EndpointCredential
        {
            EndpointCredentialId = Guid.NewGuid().ToString(),
            Username = "user-b",
            NormalizedUsername = "USER-B",
            PasswordHash = "hash-b",
            Enabled = true,
            AuthType = "password",
            CreatedUtc = now.AddSeconds(1),
            UpdatedUtc = now.AddSeconds(1),
        });
        await db.SaveChangesAsync();

        var service = new EndpointSecurityService(db);
        var result = await service.UpdateAsync(new UpdateEndpointSecurityCommand(
            Enabled: true,
            Username: null,
            Password: null,
            ActiveProfileId: null), CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.Error!.Contains("Multiple", StringComparison.OrdinalIgnoreCase));
    }
}
