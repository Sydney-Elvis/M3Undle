using System.Net;
using System.Text.Json;
using M3Undle.Web.Data;
using M3Undle.Web.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Api;

[TestClass]
public sealed class OpenApiEndpointTests
{
    [TestMethod]
    public async Task Development_OpenApiDocument_IsExposedWithExpectedMetadata()
    {
        await using var factory = new OpenApiApiFactory("Development");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/openapi/management.json");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("M3Undle Management API", json.RootElement.GetProperty("info").GetProperty("title").GetString());

        var components = json.RootElement.GetProperty("components");
        var securitySchemes = components.GetProperty("securitySchemes");
        Assert.IsTrue(securitySchemes.TryGetProperty("cookieAuth", out _));

        var paths = json.RootElement.GetProperty("paths");
        bool foundProvidersListOperation = false;
        foreach (var path in paths.EnumerateObject())
        {
            if (!path.Name.StartsWith("/api/v1/providers", StringComparison.Ordinal))
                continue;

            if (!path.Value.TryGetProperty("get", out var getOperation))
                continue;

            var summary = getOperation.TryGetProperty("summary", out var summaryNode)
                ? summaryNode.GetString()
                : null;
            if (!string.Equals(summary, "List providers", StringComparison.Ordinal))
                continue;

            if (getOperation.TryGetProperty("tags", out var tagsNode))
            {
                var tags = tagsNode.EnumerateArray().Select(x => x.GetString()).ToList();
                if (tags.Contains("Providers"))
                {
                    foundProvidersListOperation = true;
                    break;
                }
            }
        }

        Assert.IsTrue(foundProvidersListOperation, "Expected tagged/summary metadata for providers list operation.");
        Assert.IsTrue(
            paths.TryGetProperty("/api/v1/settings/streaming", out var streamingSettingsPath)
            && streamingSettingsPath.TryGetProperty("get", out _)
            && streamingSettingsPath.TryGetProperty("put", out _),
            "Expected streaming settings API operations in management OpenAPI.");
    }

    [TestMethod]
    public async Task Development_OpenApiDocument_ExcludesClientAndHealthEndpoints()
    {
        await using var factory = new OpenApiApiFactory("Development");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/management.json");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var pathKeys = json.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .Select(x => x.Name)
            .ToList();

        Assert.IsFalse(pathKeys.Any(p => string.Equals(p, "/health", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(pathKeys.Any(p => p.Contains("player_api.php", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(pathKeys.Any(p => p.Contains("get.php", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(pathKeys.Any(p => p.StartsWith("/live", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(pathKeys.Any(p => p.StartsWith("/movie", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(pathKeys.Any(p => p.StartsWith("/series", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(pathKeys.Any(p => p.StartsWith("/hls", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(pathKeys.Any(p => p.StartsWith("/hdhr", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Production_OpenApiAndScalar_AreNotExposed()
    {
        await using var factory = new OpenApiApiFactory("Production");
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var openApiResponse = await client.GetAsync("/openapi/management.json");
        Assert.AreEqual(HttpStatusCode.NotFound, openApiResponse.StatusCode);

        using var scalarResponse = await client.GetAsync("/scalar/management");
        Assert.AreEqual(HttpStatusCode.NotFound, scalarResponse.StatusCode);
    }

    private sealed class OpenApiApiFactory(string environmentName) : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-openapi-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDataDir);
            builder.UseEnvironment(environmentName);

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
