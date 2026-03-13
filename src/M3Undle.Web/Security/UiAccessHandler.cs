using M3Undle.Web.Application;
using Microsoft.AspNetCore.Authorization;

namespace M3Undle.Web.Security;

internal sealed class UiAccessHandler(ISiteSettingsService siteSettings) : AuthorizationHandler<UiAccessRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, UiAccessRequirement requirement)
    {
        if (!await siteSettings.GetAuthenticationEnabledAsync())
        {
            context.Succeed(requirement);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
        }
    }
}
