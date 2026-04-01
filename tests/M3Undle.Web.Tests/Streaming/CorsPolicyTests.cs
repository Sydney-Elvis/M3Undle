using M3Undle.Web.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using M3Undle.Web.Tests.TestSupport;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class CorsPolicyTests
{
    [TestMethod]
    public async Task MediaSurface_AllowsAnyOrigin()
    {
        await using var factory = new CorsApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/m3u/m3undle.m3u");
        request.Headers.TryAddWithoutValidation("Origin", "https://browser.example");
        using var response = await client.SendAsync(request);

        Assert.IsTrue(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.AreEqual("*", values.Single());
    }

    [TestMethod]
    public async Task ApplicationSurface_AllowedOrigin_IsReturned()
    {
        await using var factory = new CorsApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/endpoint-security");
        request.Headers.TryAddWithoutValidation("Origin", "https://app.allowed.example");
        using var response = await client.SendAsync(request);

        Assert.IsTrue(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.AreEqual("https://app.allowed.example", values.Single());
    }

    [TestMethod]
    public async Task ApplicationSurface_DisallowedOrigin_IsNotReturned()
    {
        await using var factory = new CorsApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/settings/endpoint-security");
        request.Headers.TryAddWithoutValidation("Origin", "https://not-allowed.example");
        using var response = await client.SendAsync(request);

        Assert.IsFalse(response.Headers.TryGetValues("Access-Control-Allow-Origin", out _));
    }

    private sealed class CorsApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-cors-tests-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDataDir);

            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["M3Undle:Paths:DataDirectory"] = _tempDataDir,
                    ["M3Undle:Cors:ApplicationAllowedOrigins:0"] = "https://app.allowed.example",
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
