using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace M3Undle.Web.Api;

public static class DashboardApiEndpoints
{
    public static IEndpointRouteBuilder MapDashboardApiEndpoints(this IEndpointRouteBuilder app)
    {
        var dashboard = app.MapGroup("/api/v1/dashboard");
        dashboard.RequireAuthorization(UiAccessPolicy.Name);
        dashboard.WithTags("Dashboard");
        dashboard.MapGet("/stats", GetStatsAsync).WithSummary("Get dashboard stats");
        return app;
    }

    private static async Task<Ok<DashboardStatsDto>> GetStatsAsync(
        DashboardStatsService statsService,
        CancellationToken cancellationToken)
    {
        var stats = await statsService.GetStatsAsync(cancellationToken);
        return TypedResults.Ok(stats);
    }
}
