using M3Undle.Web.Application.Notifications;
using M3Undle.Web.Contracts.Notifications;
using M3Undle.Web.Data.Entities;
using M3Undle.Web.Security;

namespace M3Undle.Web.Api;

/// <summary>
/// Administrator notification configuration. Follows the application's UiAccess policy (so it honours optional UI
/// authentication) and leaves the anonymous compatibility routes untouched. State-changing requests must carry the
/// <c>X-Requested-With</c> header, which a cross-site form post cannot set, so cookie-authenticated browsers are protected
/// from CSRF while scripts remain easy to write. Expensive actions are throttled inside the shared page service.
/// </summary>
public static class NotificationApiEndpoints
{
    public const string CsrfHeader = "X-Requested-With";

    public static IEndpointRouteBuilder MapNotificationApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications");
        group.RequireAuthorization(UiAccessPolicy.Name);
        group.WithTags("Notifications");

        group.MapGet("/", GetOverviewAsync).WithSummary("Notification catalog, routes, redacted setup and status");
        group.MapGet("/deliveries", GetDeliveriesAsync).WithSummary("Bounded delivery history");

        var changes = group.MapGroup(string.Empty);
        changes.AddEndpointFilter(RequireCsrfHeaderAsync);
        changes.MapPut("/settings", UpdateSettingsAsync).WithSummary("Update global notification state and defaults");
        changes.MapPut("/routes/{key}", UpdateRouteAsync).WithSummary("Choose Off, Matrix or Email for one notification");
        changes.MapPut("/destinations/{kind}", SaveDestinationAsync).WithSummary("Save a method's setup");
        changes.MapPost("/destinations/{kind}/test", TestDestinationAsync).WithSummary("Send real test messages for the saved setup");
        changes.MapPut("/destinations/{kind}/enabled", SetEnabledAsync).WithSummary("Enable or disable a verified method");
        changes.MapPost("/deliveries/{id}/retry", RetryAsync).WithSummary("Retry a failed or uncertain delivery");
        changes.MapPost("/deliveries/{id}/dismiss", DismissAsync).WithSummary("Dismiss a failed or uncertain delivery record");
        return app;
    }

    private static async ValueTask<object?> RequireCsrfHeaderAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.Headers.ContainsKey(CsrfHeader))
            return TypedResults.BadRequest($"Missing required {CsrfHeader} header.");
        return await next(context);
    }

    private static async Task<IResult> GetOverviewAsync(NotificationsPageService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetOverviewAsync(cancellationToken));

    private static async Task<IResult> GetDeliveriesAsync(
        NotificationsPageService service, string? state, string? provider, string? key, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (state is not null && !IsKnownState(state))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["state"] = ["Unknown delivery state."] });
        if (key is not null && !NotificationCatalog.IsKnown(key))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["key"] = ["Unknown notification."] });

        return TypedResults.Ok(await service.GetDeliveriesAsync(state, provider, key, page ?? 1, pageSize ?? 25, cancellationToken));
    }

    private static async Task<IResult> UpdateSettingsAsync(NotificationSettingsRequest request, NotificationsPageService service, CancellationToken cancellationToken) =>
        ToResult(await service.UpdateSettingsAsync(request, cancellationToken));

    private static async Task<IResult> UpdateRouteAsync(string key, NotificationRouteRequest request, NotificationsPageService service, CancellationToken cancellationToken) =>
        ToResult(await service.UpdateRouteAsync(key, request, cancellationToken));

    private static async Task<IResult> SaveDestinationAsync(string kind, NotificationDestinationRequest request, NotificationsPageService service, CancellationToken cancellationToken) =>
        ToResult(await service.SaveDestinationAsync(kind, request, cancellationToken));

    private static async Task<IResult> SetEnabledAsync(string kind, NotificationEnabledRequest request, NotificationsPageService service, CancellationToken cancellationToken) =>
        ToResult(await service.SetEnabledAsync(kind, request.Enabled, request.ExpectedRevision, cancellationToken));

    private static async Task<IResult> RetryAsync(string id, NotificationRetryRequest request, NotificationsPageService service, CancellationToken cancellationToken) =>
        ToResult(await service.RetryDeliveryAsync(id, request.ExpectedRevision, request.AcknowledgeDuplicateRisk, cancellationToken));

    private static async Task<IResult> DismissAsync(string id, NotificationRevisionRequest request, NotificationsPageService service, CancellationToken cancellationToken) =>
        ToResult(await service.DismissDeliveryAsync(id, request.ExpectedRevision, cancellationToken));

    private static async Task<IResult> TestDestinationAsync(string kind, NotificationRevisionRequest request, NotificationsPageService service, CancellationToken cancellationToken)
    {
        var result = await service.TestDestinationAsync(kind, request.ExpectedRevision, cancellationToken);
        if (result.Status != NotificationOperationStatus.Ok)
            return ToResult(new NotificationOperationResult(result.Status, result.Summary, RetryAfter: result.RetryAfter));

        return TypedResults.Ok(new NotificationTestResponse(
            result.VerificationStatus, result.Summary,
            result.Targets.Select(t => new NotificationTestTargetDto(t.Label, t.Outcome, t.ErrorCode)).ToList(),
            result.TestedRevision, result.VerificationApplied));
    }

    private static IResult ToResult(NotificationOperationResult result) => result.Status switch
    {
        NotificationOperationStatus.Ok => TypedResults.Ok(new NotificationChangeResponse(result.Message, result.Revision)),
        NotificationOperationStatus.NotFound => TypedResults.NotFound(),
        NotificationOperationStatus.Conflict => TypedResults.Conflict(new NotificationChangeResponse(result.Message, null)),
        NotificationOperationStatus.RateLimited => new RateLimitedResult(result.Message, result.RetryAfter),
        _ => TypedResults.ValidationProblem(
            result.Errors ?? new Dictionary<string, string[]> { ["request"] = [result.Message ?? "The request is not valid."] },
            detail: result.Message),
    };

    private static bool IsKnownState(string state) =>
        state is NotificationDeliveryStates.Pending or NotificationDeliveryStates.Claimed or NotificationDeliveryStates.RetryScheduled
            or NotificationDeliveryStates.Accepted or NotificationDeliveryStates.Failed or NotificationDeliveryStates.Uncertain
            or NotificationDeliveryStates.Suppressed or NotificationDeliveryStates.Dismissed;

    private sealed class RateLimitedResult(string? message, TimeSpan? retryAfter) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (retryAfter is { } wait)
                httpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return httpContext.Response.WriteAsJsonAsync(new NotificationChangeResponse(message, null));
        }
    }
}
