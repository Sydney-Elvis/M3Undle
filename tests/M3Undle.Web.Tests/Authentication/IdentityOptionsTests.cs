using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Authentication;

[TestClass]
public sealed class IdentityOptionsTests
{
    [TestMethod]
    public async Task AdaptiveLockoutOptions_AreConfiguredForStagedLockout()
    {
        await using var factory = new IdentityOptionsFactory();

        await using var scope = factory.Services.CreateAsyncScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<AdaptiveLockoutOptions>>().Value;

        Assert.AreEqual(5, options.InitialFailedAttempts);
        Assert.AreEqual(TimeSpan.FromSeconds(30), options.InitialLockoutTimeSpan);
        Assert.AreEqual(3, options.EscalatedFailedAttempts);
        Assert.AreEqual(TimeSpan.FromMinutes(5), options.EscalatedLockoutTimeSpan);
    }

    private sealed class IdentityOptionsFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-identity-options-{Guid.NewGuid():N}");

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

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await WebApplicationFactoryTestCleanup.DeleteDirectoryWhenUnlockedAsync(_tempDataDir);
        }
    }
}
