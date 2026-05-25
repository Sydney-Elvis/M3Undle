using System.Net;
using System.Text;
using M3Undle.Web.Application;
using M3Undle.Web.Data;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using M3Undle.Web.Tests.TestSupport;

namespace M3Undle.Web.Tests.Streaming;

[TestClass]
public sealed class HlsProxyEndpointSecurityTests
{
    [TestMethod]
    public async Task CompatibilityHlsProxy_UnknownStreamKey_ReturnsNotFound_EvenWithProviderParam()
    {
        await using var factory = new HlsProxyApiFactory();
        using var client = factory.CreateClient();

        var u = EncodeBase64Url("http://127.0.0.1:65535/segment.ts");
        using var response = await client.GetAsync($"/hls/non-existent-key/proxy?u={u}&p=provider-override");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task XtreamHlsProxy_UnknownStreamKey_ReturnsNotFound_EvenWithProviderParam()
    {
        await using var factory = new HlsProxyApiFactory();
        using var client = factory.CreateClient();

        var u = EncodeBase64Url("http://127.0.0.1:65535/segment.ts");
        using var response = await client.GetAsync($"/hls/test-user/test-pass/non-existent-key/proxy?u={u}&p=provider-override");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string EncodeBase64Url(string value)
        => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));

    private sealed class HlsProxyApiFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"m3undle-hls-proxy-security-{Guid.NewGuid():N}");

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
                if (dbDescriptor != null)
                    services.Remove(dbDescriptor);
                services.AddDbContext<ApplicationDbContext>(options =>
                    options.UseSqlite(WebApplicationFactoryTestCleanup.CreateSqliteConnectionString(_tempDataDir))
                           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

                services.RemoveAll<IAccessResolver>();
                services.AddScoped<IAccessResolver, StubAccessResolver>();

                services.RemoveAll<IEndpointSecurityService>();
                services.AddScoped<IEndpointSecurityService, StubEndpointSecurityService>();

                services.RemoveAll<IProfileResolver>();
                services.AddScoped<IProfileResolver, StubProfileResolver>();
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await WebApplicationFactoryTestCleanup.DeleteDirectoryWhenUnlockedAsync(_tempDataDir);
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

    private sealed class StubEndpointSecurityService : IEndpointSecurityService
    {
        public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(false);

        public ValueTask<bool> IsXtreamEnabledAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(true);

        public Task<EndpointSecuritySettings> GetSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult(new EndpointSecuritySettings(
                Enabled: false,
                Username: null,
                HasCredential: false,
                ActiveProfileId: "profile-1",
                VirtualTunerId: "hdhr-main",
                XtreamCompatibilityEnabled: true));

        public Task<EndpointBindingState?> GetBindingAsync(string credentialId, CancellationToken cancellationToken)
            => Task.FromResult<EndpointBindingState?>(new EndpointBindingState("profile-1", "hdhr-main"));

        public Task<EndpointSecurityUpdateResult> UpdateAsync(UpdateEndpointSecurityCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class StubProfileResolver : IProfileResolver
    {
        public ValueTask<string?> ResolveActiveProfileIdAsync(string? requestedProfileId, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>("profile-1");
    }
}
