using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace M3Undle.Web.Api;

public static class CustomGroupApiEndpoints
{
    public static IEndpointRouteBuilder MapCustomGroupApiEndpoints(this IEndpointRouteBuilder app)
    {
        var groups = app.MapGroup("/api/v1/profiles/{profileId}/custom-groups");
        groups.RequireAuthorization(UiAccessPolicy.Name);
        groups.WithTags("Custom Groups");

        groups.MapGet("/", ListAsync).WithSummary("List custom groups");
        groups.MapPost("/", CreateAsync).WithSummary("Create a custom group");
        groups.MapPatch("/{customGroupId}", UpdateAsync).WithSummary("Update a custom group");
        groups.MapDelete("/{customGroupId}", DeleteAsync).WithSummary("Delete a custom group");

        groups.MapGet("/{customGroupId}/channels", ListChannelsAsync).WithSummary("List channels in a custom group");
        groups.MapGet("/{customGroupId}/channel-search", SearchChannelsAsync).WithSummary("Search channels addable to a custom group");
        groups.MapPost("/{customGroupId}/channels", AddChannelsAsync).WithSummary("Add channels to a custom group");
        groups.MapDelete("/{customGroupId}/channels/{providerChannelId}", RemoveChannelAsync).WithSummary("Remove channel from a custom group");
        groups.MapPatch("/{customGroupId}/channels/{providerChannelId}", UpdateChannelAsync).WithSummary("Update custom group channel");

        groups.MapGet("/{customGroupId}/provider-links", ListProviderLinksAsync).WithSummary("List provider links");
        groups.MapPost("/{customGroupId}/provider-links", AddProviderLinkAsync).WithSummary("Add provider link");
        groups.MapDelete("/{customGroupId}/provider-links/{providerGroupId}", RemoveProviderLinkAsync).WithSummary("Remove provider link");

        return app;
    }

    private static async Task<Ok<List<CustomGroupDto>>> ListAsync(
        string profileId,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await svc.ListAsync(profileId, cancellationToken));

    private static async Task<Results<Ok<CustomGroupDto>, BadRequest, NotFound>> CreateAsync(
        string profileId,
        CreateCustomGroupRequest request,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return TypedResults.BadRequest();

        var result = await svc.CreateAsync(profileId, request.Name, cancellationToken);
        if (result is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(result);
    }

    private static async Task<Results<NoContent, NotFound>> UpdateAsync(
        string profileId,
        string customGroupId,
        UpdateCustomGroupRequest request,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        var updated = await svc.UpdateAsync(profileId, customGroupId, request, cancellationToken);
        return updated ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        string profileId,
        string customGroupId,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        var deleted = await svc.DeleteAsync(profileId, customGroupId, cancellationToken);
        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<List<CustomGroupChannelDto>>, NotFound>> ListChannelsAsync(
        string profileId,
        string customGroupId,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        var channels = await svc.ListChannelsAsync(profileId, customGroupId, cancellationToken);
        return TypedResults.Ok(channels);
    }

    private static async Task<Ok<List<CustomGroupChannelSearchResultDto>>> SearchChannelsAsync(
        string profileId,
        string customGroupId,
        string? q,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await svc.SearchAddableChannelsAsync(profileId, customGroupId, q, cancellationToken));

    private static async Task<Results<Ok<int>, NotFound>> AddChannelsAsync(
        string profileId,
        string customGroupId,
        AddCustomGroupChannelsRequest request,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        if (request.ProviderChannelIds.Count == 0)
            return TypedResults.Ok(0);

        var added = await svc.AddChannelsAsync(profileId, customGroupId, request.ProviderChannelIds, cancellationToken);
        return TypedResults.Ok(added);
    }

    private static async Task<Results<NoContent, NotFound>> RemoveChannelAsync(
        string profileId,
        string customGroupId,
        string providerChannelId,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        var removed = await svc.RemoveChannelAsync(profileId, customGroupId, providerChannelId, cancellationToken);
        return removed ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound>> UpdateChannelAsync(
        string profileId,
        string customGroupId,
        string providerChannelId,
        UpdateCustomGroupChannelRequest request,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        var updated = await svc.UpdateChannelAsync(profileId, customGroupId, providerChannelId, request, cancellationToken);
        return updated ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Ok<List<CustomGroupProviderLinkDto>>> ListProviderLinksAsync(
        string profileId,
        string customGroupId,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
        => TypedResults.Ok(await svc.ListProviderLinksAsync(profileId, customGroupId, cancellationToken));

    private static async Task<Results<NoContent, NotFound>> AddProviderLinkAsync(
        string profileId,
        string customGroupId,
        AddCustomGroupProviderLinkRequest request,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderGroupId))
            return TypedResults.NotFound();

        var added = await svc.AddProviderLinkAsync(profileId, customGroupId, request.ProviderGroupId, cancellationToken);
        return added ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static async Task<Results<NoContent, NotFound>> RemoveProviderLinkAsync(
        string profileId,
        string customGroupId,
        string providerGroupId,
        CustomGroupPageService svc,
        CancellationToken cancellationToken)
    {
        var removed = await svc.RemoveProviderLinkAsync(profileId, customGroupId, providerGroupId, cancellationToken);
        return removed ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
