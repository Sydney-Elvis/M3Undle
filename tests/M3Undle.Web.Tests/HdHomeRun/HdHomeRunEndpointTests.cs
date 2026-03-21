using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.HdHomeRun;

[TestClass]
public sealed class HdHomeRunEndpointTests
{
    [TestMethod]
    public async Task Endpoint_DiscoverJson_ReturnsStableDeviceIdentity()
    {
        // Checklist: /hdhr/discover.json returns stable device identity and correct TunerCount.
        await using var factory = new HdhrApiFactory();
        using var client = factory.CreateClient();

        using var firstResponse = await client.GetAsync("/hdhr/discover.json");
        using var secondResponse = await client.GetAsync("/hdhr/discover.json");

        Assert.AreEqual(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, secondResponse.StatusCode);

        using var firstJson = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        using var secondJson = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());

        var firstDeviceId = firstJson.RootElement.GetProperty("DeviceID").GetString();
        var secondDeviceId = secondJson.RootElement.GetProperty("DeviceID").GetString();
        var tunerCount = firstJson.RootElement.GetProperty("TunerCount").GetInt32();

        Assert.IsFalse(string.IsNullOrWhiteSpace(firstDeviceId));
        Assert.AreEqual(8, firstDeviceId!.Length);
        Assert.IsTrue(firstDeviceId.All(Uri.IsHexDigit));
        Assert.IsGreaterThan(0, tunerCount);
        Assert.AreEqual(firstDeviceId, secondDeviceId);
    }

    [TestMethod]
    public async Task Endpoint_LineupJson_ReturnsLiveChannelsOnly()
    {
        // Checklist: /hdhr/lineup.json returns only live channels with stable guide numbers and tune URLs.
        await using var factory = new HdhrApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/hdhr/lineup.json");
        Assert.IsTrue(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            return;

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var channels = json.RootElement.EnumerateArray().ToArray();
        Assert.IsNotEmpty(channels);

        foreach (var channel in channels)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(channel.GetProperty("GuideName").GetString()));
            Assert.IsFalse(string.IsNullOrWhiteSpace(channel.GetProperty("GuideNumber").GetString()));
            var url = channel.GetProperty("URL").GetString();
            Assert.IsFalse(string.IsNullOrWhiteSpace(url));
            StringAssert.Contains(url!, "/hdhr/tune/");
        }

        var names = channels
            .Select(x => x.GetProperty("GuideName").GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        CollectionAssert.DoesNotContain(names, "Movie One");
    }

    [TestMethod]
    public async Task Endpoint_LineupXml_MatchesLineupJson()
    {
        // Checklist: /hdhr/lineup.xml matches /hdhr/lineup.json.
        await using var factory = new HdhrApiFactory();
        using var client = factory.CreateClient();

        using var jsonResponse = await client.GetAsync("/hdhr/lineup.json");
        using var xmlResponse = await client.GetAsync("/hdhr/lineup.xml");

        if (jsonResponse.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, xmlResponse.StatusCode);
            return;
        }

        Assert.AreEqual(HttpStatusCode.OK, jsonResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, xmlResponse.StatusCode);

        var jsonNames = await ReadLineupJsonNamesAsync(jsonResponse);
        var xmlNames = await ReadLineupXmlNamesAsync(xmlResponse);

        Assert.HasCount(jsonNames.Count, xmlNames);
        CollectionAssert.AreEquivalent(jsonNames.ToArray(), xmlNames.ToArray());
    }

    [TestMethod]
    public async Task Endpoint_LineupM3u_MatchesLineupJson()
    {
        // Checklist: /hdhr/lineup.m3u matches /hdhr/lineup.json.
        await using var factory = new HdhrApiFactory();
        using var client = factory.CreateClient();

        using var jsonResponse = await client.GetAsync("/hdhr/lineup.json");
        using var m3uResponse = await client.GetAsync("/hdhr/lineup.m3u");

        if (jsonResponse.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, m3uResponse.StatusCode);
            return;
        }

        Assert.AreEqual(HttpStatusCode.OK, jsonResponse.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, m3uResponse.StatusCode);

        var jsonNames = await ReadLineupJsonNamesAsync(jsonResponse);
        var m3uNames = await ReadLineupM3uNamesAsync(m3uResponse);

        Assert.HasCount(jsonNames.Count, m3uNames);
        CollectionAssert.AreEquivalent(jsonNames.ToArray(), m3uNames.ToArray());
    }

    [TestMethod]
    public async Task Endpoint_LineupStatus_ReportsReadiness()
    {
        // Checklist: /hdhr/lineup_status.json reports lineup readiness when an active snapshot exists.
        await using var factory = new HdhrApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/hdhr/lineup_status.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.IsTrue(json.RootElement.TryGetProperty("ScanInProgress", out var scanInProgress));
        Assert.AreEqual(JsonValueKind.Number, scanInProgress.ValueKind);
        Assert.IsTrue(json.RootElement.TryGetProperty("Status", out var status));
        Assert.IsFalse(string.IsNullOrWhiteSpace(status.GetString()));
    }

    [TestMethod]
    public async Task Endpoint_DeviceXml_ReturnsWellFormedXml()
    {
        // Checklist: /hdhr/device.xml loads successfully from a client.
        await using var factory = new HdhrApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/hdhr/device.xml");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Content.Headers.ContentType?.MediaType is "application/xml" or "text/xml");

        var xmlText = await response.Content.ReadAsStringAsync();
        var document = XDocument.Parse(xmlText);

        Assert.AreEqual("root", document.Root?.Name.LocalName);
        Assert.IsNotNull(document.Root?.Descendants().FirstOrDefault(x => x.Name.LocalName == "friendlyName"));
        Assert.IsNotNull(document.Root?.Descendants().FirstOrDefault(x => x.Name.LocalName == "serialNumber"));
    }

    [TestMethod]
    public async Task Endpoint_LegacyAliases_ReturnSameStatusAsHdhrRoutes()
    {
        // Checklist: Legacy aliases behave the same as /hdhr/*.
        await using var factory = new HdhrApiFactory();
        using var client = factory.CreateClient();

        var routePairs = new (string Legacy, string Hdhr)[]
        {
            ("/discover.json", "/hdhr/discover.json"),
            ("/lineup.json", "/hdhr/lineup.json"),
            ("/lineup.xml", "/hdhr/lineup.xml"),
            ("/lineup.m3u", "/hdhr/lineup.m3u"),
            ("/lineup_status.json", "/hdhr/lineup_status.json"),
            ("/device.xml", "/hdhr/device.xml"),
        };

        foreach (var (legacy, hdhr) in routePairs)
        {
            using var legacyResponse = await client.GetAsync(legacy);
            using var hdhrResponse = await client.GetAsync(hdhr);

            Assert.AreEqual(hdhrResponse.StatusCode, legacyResponse.StatusCode, $"Status mismatch: {legacy} vs {hdhr}");

            var legacyBody = await legacyResponse.Content.ReadAsStringAsync();
            var hdhrBody = await hdhrResponse.Content.ReadAsStringAsync();
            Assert.AreEqual(hdhrBody, legacyBody, $"Body mismatch: {legacy} vs {hdhr}");
        }
    }

    private static async Task<List<string>> ReadLineupJsonNamesAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement
            .EnumerateArray()
            .Select(x => x.GetProperty("GuideName").GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }

    private static async Task<List<string>> ReadLineupXmlNamesAsync(HttpResponseMessage response)
    {
        var xmlText = await response.Content.ReadAsStringAsync();
        var document = XDocument.Parse(xmlText);

        return document
            .Descendants()
            .Where(x => x.Name.LocalName == "GuideName")
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static async Task<List<string>> ReadLineupM3uNamesAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            .Select(line =>
            {
                var commaIndex = line.IndexOf(',');
                return commaIndex >= 0 ? line[(commaIndex + 1)..] : string.Empty;
            })
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private sealed class HdhrApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-hdhr-endpoints-{Guid.NewGuid():N}");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(_tempDataDir);

            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["M3Undle:Paths:DataDirectory"] = _tempDataDir,
                    ["M3Undle:HdHomeRun:Enabled"] = "true",
                    ["M3Undle:HdHomeRun:TunerCount"] = "2",
                });
            });

            builder.ConfigureTestServices(services =>
            {
                // EF Core 10 validates migrations against a fresh database when Migrate() is called.
                // Replace the DbContext options to suppress the false-positive PendingModelChangesWarning
                // (confirmed: dotnet ef migrations has-pending-model-changes reports no changes).
                var dbDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (dbDescriptor != null)
                    services.Remove(dbDescriptor);
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite($"Data Source={Path.Combine(_tempDataDir, "m3undle.db")}")
                           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

                services.RemoveAll<IAccessResolver>();
                services.AddScoped<IAccessResolver, StubAccessResolver>();

                services.RemoveAll<ILineupRenderer>();
                services.AddScoped<ILineupRenderer, StubLineupRenderer>();
            });
        }

        public new async ValueTask DisposeAsync()
        {
            Dispose();
            await Task.CompletedTask;

            if (Directory.Exists(_tempDataDir))
                Directory.Delete(_tempDataDir, recursive: true);
        }
    }

    private sealed class StubAccessResolver : IAccessResolver
    {
        public ValueTask<ClientAccessResolutionResult> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var credential = new AccessCredential(
                Id: "test-credential",
                Username: "test-user",
                PasswordHash: string.Empty,
                Enabled: true,
                AuthType: AccessCredentialAuthType.UsernamePassword);

            var access = new ResolvedClientAccess(
                Credential: credential,
                Binding: new AccessBinding(
                    CredentialId: credential.Id,
                    ActiveProfileId: "profile-1",
                    AllowedProfileIds: ["profile-1"],
                    VirtualTunerId: "hdhr-main"),
                Transport: ClientCredentialTransport.None,
                UrlCredential: null);

            return ValueTask.FromResult(ClientAccessResolutionResult.Success(access));
        }
    }

    private sealed class StubLineupRenderer : ILineupRenderer
    {
        public Task<RenderedLineup?> TryRenderActiveLineupAsync(string? profileId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<RenderedLineup?>(new RenderedLineup(
                SnapshotId: "snapshot-1",
                ProfileId: profileId ?? "profile-1",
                SnapshotCreatedUtc: DateTime.UtcNow,
                ChannelIndexPath: "unused",
                XmltvPath: null,
                Channels:
                [
                    new RenderedLineupChannel("live-1", "Alpha", "alpha.tv", "Alpha", null, "News", 11, "http://example.com/live/alpha.ts", "live"),
                    new RenderedLineupChannel("vod-1", "Movie One", "movie.one", "Movie One", null, "Movies", null, "http://example.com/movie/one.mkv", "vod"),
                    new RenderedLineupChannel("live-2", "Bravo", "bravo.tv", "Bravo HD", null, "News", null, "http://example.com/live/bravo.ts", "live"),
                ]));
        }
    }
}
