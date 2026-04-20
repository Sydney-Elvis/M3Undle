using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace M3Undle.Web.Api;

public static class DownstreamApiEndpoints
{
    public static IEndpointRouteBuilder MapDownstreamApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/downstream/integrations");
        group.RequireAuthorization(UiAccessPolicy.Name);
        group.WithTags("Downstream");

        group.MapGet("/", ListAsync).WithSummary("List downstream integrations");
        group.MapGet("/{id}", GetAsync).WithSummary("Get a downstream integration");
        group.MapPost("/", CreateAsync).WithSummary("Create a downstream integration");
        group.MapPut("/{id}", UpdateAsync).WithSummary("Update a downstream integration");
        group.MapDelete("/{id}", DeleteAsync).WithSummary("Delete a downstream integration");

        return app;
    }

    private static async Task<Ok<List<DownstreamIntegrationDto>>> ListAsync(
        IDownstreamIntegrationService service,
        CancellationToken cancellationToken)
    {
        var items = await service.GetAllAsync(cancellationToken);
        return TypedResults.Ok(items);
    }

    private static async Task<Results<Ok<DownstreamIntegrationDto>, NotFound>> GetAsync(
        string id,
        IDownstreamIntegrationService service,
        CancellationToken cancellationToken)
    {
        var item = await service.GetAsync(id, cancellationToken);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item);
    }

    private static async Task<Results<Created<DownstreamIntegrationDto>, ValidationProblem>> CreateAsync(
        CreateDownstreamIntegrationRequest request,
        IDownstreamIntegrationService service,
        CancellationToken cancellationToken)
    {
        var (result, error) = await service.CreateAsync(request, cancellationToken);
        if (error is not null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["integration"] = [error],
            });

        return TypedResults.Created($"/api/v1/downstream/integrations/{result!.DownstreamIntegrationId}", result);
    }

    private static async Task<Results<Ok<DownstreamIntegrationDto>, NotFound, ValidationProblem>> UpdateAsync(
        string id,
        UpdateDownstreamIntegrationRequest request,
        IDownstreamIntegrationService service,
        CancellationToken cancellationToken)
    {
        var (result, error) = await service.UpdateAsync(id, request, cancellationToken);
        if (error == "Integration not found.")
            return TypedResults.NotFound();
        if (error is not null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["integration"] = [error],
            });

        return TypedResults.Ok(result);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> DeleteAsync(
        string id,
        IDownstreamIntegrationService service,
        CancellationToken cancellationToken)
    {
        var error = await service.DeleteAsync(id, cancellationToken);
        if (error == "Integration not found.")
            return TypedResults.NotFound();
        if (error is not null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["integration"] = [error],
            });

        return TypedResults.NoContent();
    }
}
