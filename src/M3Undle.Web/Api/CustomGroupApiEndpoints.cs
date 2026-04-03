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

        groups.MapGet("/", ListAsync);
        groups.MapPost("/", CreateAsync);
        groups.MapPatch("/{customGroupId}", UpdateAsync);
        groups.MapDelete("/{customGroupId}", DeleteAsync);

        groups.MapGet("/{customGroupId}/channels", ListChannelsAsync);
        groups.MapPost("/{customGroupId}/channels", AddChannelsAsync);
        groups.MapDelete("/{customGroupId}/channels/{providerChannelId}", RemoveChannelAsync);
        groups.MapPatch("/{customGroupId}/channels/{providerChannelId}", UpdateChannelAsync);

        groups.MapGet("/{customGroupId}/provider-links", ListProviderLinksAsync);
        groups.MapPost("/{customGroupId}/provider-links", AddProviderLinkAsync);
        groups.MapDelete("/{customGroupId}/provider-links/{providerGroupId}", RemoveProviderLinkAsync);

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
