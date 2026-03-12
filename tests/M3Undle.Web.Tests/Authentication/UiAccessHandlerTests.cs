using System.Security.Claims;
using M3Undle.Web.Application;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Authentication;

[TestClass]
public sealed class UiAccessHandlerTests
{
    [TestMethod]
    public async Task AllowsAnonymousUser_WhenAuthenticationIsDisabled()
    {
        var handler = new UiAccessHandler(new StubSiteSettingsService(authenticationEnabled: false));
        var requirement = new UiAccessRequirement();
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);

        await handler.HandleAsync(context);

        Assert.IsTrue(context.HasSucceeded);
    }

    [TestMethod]
    public async Task RejectsAnonymousUser_WhenAuthenticationIsEnabled()
    {
        var handler = new UiAccessHandler(new StubSiteSettingsService(authenticationEnabled: true));
        var requirement = new UiAccessRequirement();
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);

        await handler.HandleAsync(context);

        Assert.IsFalse(context.HasSucceeded);
    }

    [TestMethod]
    public async Task AllowsAuthenticatedUser_WhenAuthenticationIsEnabled()
    {
        var handler = new UiAccessHandler(new StubSiteSettingsService(authenticationEnabled: true));
        var requirement = new UiAccessRequirement();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner@example.com")], authenticationType: "Cookies");
        var user = new ClaimsPrincipal(identity);
        var context = new AuthorizationHandlerContext([requirement], user, resource: null);

        await handler.HandleAsync(context);

        Assert.IsTrue(context.HasSucceeded);
    }

    private sealed class StubSiteSettingsService(bool authenticationEnabled) : ISiteSettingsService
    {
        public ValueTask<bool> GetAuthenticationEnabledAsync() => ValueTask.FromResult(authenticationEnabled);
    }
}
