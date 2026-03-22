using System.Net;
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

namespace M3Undle.Web.Tests.Xmltv;

/// <summary>
/// Checklist: Published /xmltv/m3undle.xml matches the active lineup.
/// Endpoint-level tests using a stub ILineupRenderer that returns a known XMLTV file
/// and lineup channels. Verifies the full path: endpoint → renderer → serializer →
/// correct file served with content matching the active lineup.
/// </summary>
[TestClass]
public sealed class XmltvEndpointTests
{
    [TestMethod]
    public async Task Endpoint_XmltvFile_Returns200WithApplicationXml()
    {
        await using var factory = new XmltvApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/xmltv/m3undle.xml");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        StringAssert.StartsWith(
            response.Content.Headers.ContentType?.ToString(),
            "application/xml");
    }

    [TestMethod]
    public async Task Endpoint_XmltvFile_ChannelIdsMatchActiveLineup()
    {
        // Verifies each channel tvg-id in the lineup appears as a <channel id="..."> in the XMLTV output.
        await using var factory = new XmltvApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/xmltv/m3undle.xml");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var channelIds = xml.Root!
            .Elements("channel")
            .Select(e => e.Attribute("id")?.Value)
            .Where(id => id is not null)
            .ToList();

        // Must match the tvg-id values in StubLineupRenderer
        Assert.Contains("alpha.tv", channelIds, "Expected channel id 'alpha.tv' in XMLTV output.");
        Assert.Contains("bravo.tv", channelIds, "Expected channel id 'bravo.tv' in XMLTV output.");
        Assert.HasCount(2, channelIds, "XMLTV should contain exactly the channels in the active lineup.");
    }

    [TestMethod]
    public async Task Endpoint_XmltvFile_DisplayNamesMatchActiveLineup()
    {
        await using var factory = new XmltvApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/xmltv/m3undle.xml");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
        var displayNames = xml.Root!
            .Elements("channel")
            .SelectMany(e => e.Elements("display-name"))
            .Select(e => e.Value)
            .ToHashSet();

        Assert.Contains("Alpha", displayNames, "Expected display-name 'Alpha' in XMLTV output.");
        Assert.Contains("Bravo", displayNames, "Expected display-name 'Bravo' in XMLTV output.");
    }

    [TestMethod]
    public async Task Endpoint_XmltvFile_Returns503WhenNoActiveSnapshot()
    {
        await using var factory = new XmltvApiFactory(returnNullLineup: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/xmltv/m3undle.xml");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------

    private sealed class XmltvApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-xmltv-{Guid.NewGuid():N}");
        private readonly string _xmltvPath;
        private readonly bool _returnNullLineup;

        public XmltvApiFactory(bool returnNullLineup = false)
        {
            _returnNullLineup = returnNullLineup;
            Directory.CreateDirectory(_tempDataDir);
            _xmltvPath = Path.Combine(_tempDataDir, "m3undle.xml");

            // Minimal XMLTV file matching the channels in StubLineupRenderer
            File.WriteAllText(_xmltvPath, """
                <?xml version="1.0" encoding="UTF-8"?>
                <tv>
                  <channel id="alpha.tv">
                    <display-name>Alpha</display-name>
                  </channel>
                  <channel id="bravo.tv">
                    <display-name>Bravo</display-name>
                  </channel>
                </tv>
                """);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
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
                if (dbDescriptor != null)
                    services.Remove(dbDescriptor);
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite($"Data Source={Path.Combine(_tempDataDir, "m3undle.db")}")
                           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

                services.RemoveAll<IAccessResolver>();
                services.AddScoped<IAccessResolver, StubAccessResolver>();

                services.RemoveAll<ILineupRenderer>();
                services.AddScoped<ILineupRenderer>(_ =>
                    new StubLineupRenderer(_returnNullLineup ? null : _xmltvPath));
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
                    VirtualTunerId: null),
                Transport: ClientCredentialTransport.None,
                UrlCredential: null);

            return ValueTask.FromResult(ClientAccessResolutionResult.Success(access));
        }
    }

    private sealed class StubLineupRenderer(string? xmltvPath) : ILineupRenderer
    {
        public Task<RenderedLineup?> TryRenderActiveLineupAsync(string? profileId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (xmltvPath is null)
                return Task.FromResult<RenderedLineup?>(null);

            return Task.FromResult<RenderedLineup?>(new RenderedLineup(
                SnapshotId: "snapshot-1",
                ProfileId: profileId ?? "profile-1",
                SnapshotCreatedUtc: DateTime.UtcNow,
                ChannelIndexPath: "unused",
                XmltvPath: xmltvPath,
                Channels:
                [
                    new RenderedLineupChannel("live-1", "Alpha", "alpha.tv", "Alpha", null, "News", 11, "http://example.com/live/alpha.ts", "live"),
                    new RenderedLineupChannel("live-2", "Bravo", "bravo.tv", "Bravo HD", null, "News", null, "http://example.com/live/bravo.ts", "live"),
                ]));
        }
    }
}
