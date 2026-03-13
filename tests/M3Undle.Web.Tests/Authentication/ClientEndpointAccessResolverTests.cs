using System.Text;
using M3Undle.Web.Application;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Authentication;

[TestClass]
public sealed class ClientEndpointAccessResolverTests
{
    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityDisabled_AllowsAnonymousContext()
    {
        var resolver = new ClientEndpointAccessResolver(
            endpointSecurityService: new StubEndpointSecurityService(enabled: false),
            credentialValidator: new StubCredentialValidator(),
            profileResolver: new StubProfileResolver("profile-1"));

        var context = new DefaultHttpContext();
        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Access);
        Assert.AreEqual("profile-1", result.Access.Binding.ActiveProfileId);
        Assert.AreEqual(ClientCredentialTransport.None, result.Access.Transport);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityEnabled_WithoutCredentials_ReturnsMissingCredentials()
    {
        var resolver = new ClientEndpointAccessResolver(
            endpointSecurityService: new StubEndpointSecurityService(enabled: true),
            credentialValidator: new StubCredentialValidator(),
            profileResolver: new StubProfileResolver("profile-1"));

        var context = new DefaultHttpContext();
        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ClientAccessFailureReason.MissingCredentials, result.FailureReason);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityEnabled_WithQueryCredentials_ResolvesBinding()
    {
        var credential = new AccessCredential(
            Id: "cred-1",
            Username: "endpoint-user",
            PasswordHash: "hash",
            Enabled: true,
            AuthType: AccessCredentialAuthType.UsernamePassword);

        var resolver = new ClientEndpointAccessResolver(
            endpointSecurityService: new StubEndpointSecurityService(
                enabled: true,
                binding: new EndpointBindingState(ActiveProfileId: "profile-2", VirtualTunerId: "living-room")),
            credentialValidator: new StubCredentialValidator(credential),
            profileResolver: new StubProfileResolver("profile-2"));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?username=endpoint-user&password=secret");

        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Access);
        Assert.AreEqual("profile-2", result.Access.Binding.ActiveProfileId);
        Assert.AreEqual("living-room", result.Access.Binding.VirtualTunerId);
        Assert.AreEqual(ClientCredentialTransport.QueryString, result.Access.Transport);
        Assert.IsNotNull(result.Access.UrlCredential);
        Assert.AreEqual("endpoint-user", result.Access.UrlCredential.Username);
        Assert.AreEqual("secret", result.Access.UrlCredential.Password);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityEnabled_WithUserPassQueryParams_ResolvesBinding()
    {
        var credential = MakeCredential();
        var resolver = BuildResolver(enabled: true, credential: credential, profileId: "profile-1");

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?user=endpoint-user&pass=secret");

        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(ClientCredentialTransport.QueryString, result.Access!.Transport);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityEnabled_WithUPQueryParams_ResolvesBinding()
    {
        var credential = MakeCredential();
        var resolver = BuildResolver(enabled: true, credential: credential, profileId: "profile-1");

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?u=endpoint-user&p=secret");

        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(ClientCredentialTransport.QueryString, result.Access!.Transport);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityEnabled_WithBasicAuthHeader_ResolvesBinding()
    {
        var credential = MakeCredential();
        var resolver = BuildResolver(enabled: true, credential: credential, profileId: "profile-1");

        var context = new DefaultHttpContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("endpoint-user:secret"));
        context.Request.Headers["Authorization"] = $"Basic {encoded}";

        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(ClientCredentialTransport.AuthorizationHeaderBasic, result.Access!.Transport);
        Assert.IsNull(result.Access.UrlCredential);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityEnabled_WithInvalidCredentials_ReturnsInvalidCredentials()
    {
        var resolver = BuildResolver(enabled: true, credential: null, profileId: "profile-1");

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?username=bad-user&password=bad-pass");

        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ClientAccessFailureReason.InvalidCredentials, result.FailureReason);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityEnabled_WithValidCredentials_ButNoActiveProfile_ReturnsNoActiveProfile()
    {
        var credential = MakeCredential();

        var resolver = new ClientEndpointAccessResolver(
            endpointSecurityService: new StubEndpointSecurityService(enabled: true),
            credentialValidator: new StubCredentialValidator(credential),
            profileResolver: new StubProfileResolver(null));

        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?username=endpoint-user&password=secret");

        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ClientAccessFailureReason.NoActiveProfile, result.FailureReason);
    }

    [TestMethod]
    public async Task ResolveAsync_WhenEndpointSecurityDisabled_AndNoActiveProfile_ReturnsNoActiveProfile()
    {
        var resolver = new ClientEndpointAccessResolver(
            endpointSecurityService: new StubEndpointSecurityService(enabled: false),
            credentialValidator: new StubCredentialValidator(),
            profileResolver: new StubProfileResolver(null));

        var context = new DefaultHttpContext();
        var result = await resolver.ResolveAsync(context, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(ClientAccessFailureReason.NoActiveProfile, result.FailureReason);
    }

    private static AccessCredential MakeCredential() => new(
        Id: "cred-1",
        Username: "endpoint-user",
        PasswordHash: "hash",
        Enabled: true,
        AuthType: AccessCredentialAuthType.UsernamePassword);

    private static ClientEndpointAccessResolver BuildResolver(
        bool enabled,
        AccessCredential? credential,
        string? profileId) => new(
            endpointSecurityService: new StubEndpointSecurityService(enabled),
            credentialValidator: new StubCredentialValidator(credential),
            profileResolver: new StubProfileResolver(profileId));

    private sealed class StubEndpointSecurityService(bool enabled, EndpointBindingState? binding = null) : IEndpointSecurityService
    {
        public ValueTask<bool> IsEnabledAsync(CancellationToken cancellationToken) => ValueTask.FromResult(enabled);

        public Task<EndpointSecuritySettings> GetSettingsAsync(CancellationToken cancellationToken)
            => Task.FromResult(new EndpointSecuritySettings(enabled, null, false, null, null));

        public Task<EndpointBindingState?> GetBindingAsync(string credentialId, CancellationToken cancellationToken)
            => Task.FromResult(binding);

        public Task<EndpointSecurityUpdateResult> UpdateAsync(UpdateEndpointSecurityCommand command, CancellationToken cancellationToken)
            => Task.FromResult(new EndpointSecurityUpdateResult(
                Succeeded: true,
                Error: null,
                Settings: new EndpointSecuritySettings(false, null, false, null, null)));
    }

    private sealed class StubCredentialValidator(AccessCredential? credential = null) : ICredentialValidator
    {
        public ValueTask<AccessCredential?> ValidateAsync(string username, string password, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(credential);
        }
    }

    private sealed class StubProfileResolver(string? profileId) : IProfileResolver
    {
        public ValueTask<string?> ResolveActiveProfileIdAsync(string? preferredProfileId, CancellationToken cancellationToken)
            => ValueTask.FromResult(profileId);
    }
}
