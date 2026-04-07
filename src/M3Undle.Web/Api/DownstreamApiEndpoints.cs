using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Security;

namespace M3Undle.Web.Api;

public static class DownstreamApiEndpoints
{
    public static IEndpointRouteBuilder MapDownstreamApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/downstream/integrations");
        group.RequireAuthorization(UiAccessPolicy.Name);

        group.MapGet("/", ListAsync);
        group.MapGet("/{id}", GetAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id}", UpdateAsync);
        group.MapDelete("/{id}", DeleteAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        IDownstreamIntegrationService service,
        CancellationToken cancellationToken)
    {
        var items = await service.GetAllAsync(cancellationToken);
        return TypedResults.Ok(items);
    }

    private static async Task<IResult> GetAsync(
        string id,
        IDownstreamIntegrationService service,
        CancellationToken cancellationToken)
    {
        var item = await service.GetAsync(id, cancellationToken);
        return item is null ? TypedResults.NotFound() : TypedResults.Ok(item);
    }

    private static async Task<IResult> CreateAsync(
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

    private static async Task<IResult> UpdateAsync(
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

    private static async Task<IResult> DeleteAsync(
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
