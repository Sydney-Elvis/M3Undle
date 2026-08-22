using M3Undle.Web.Application;
using M3Undle.Web.Application.Backup;
using M3Undle.Web.Contracts;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace M3Undle.Web.Api;

public static class RestoreApiEndpoints
{
    public static IEndpointRouteBuilder MapRestoreApiEndpoints(this IEndpointRouteBuilder app)
    {
        var restore = app.MapGroup("/api/v1/restore");
        restore.RequireAuthorization(UiAccessPolicy.Name);
        restore.WithTags("Restore");

        restore.MapPost("/stage", StageAsync).WithSummary("Validate a backup and stage it for restore on next restart")
            .RequireRateLimiting(BackupApiEndpoints.RateLimitPolicyName);
        restore.MapPost("/confirm", ConfirmAsync).WithSummary("Restart the application to apply the staged restore")
            .RequireRateLimiting(BackupApiEndpoints.RateLimitPolicyName);
        restore.MapPost("/apply-settings", ApplySettingsAsync).WithSummary("Apply a settings archive to a clean instance")
            .RequireRateLimiting(BackupApiEndpoints.RateLimitPolicyName);
        restore.MapDelete("/stage", CancelStaged).WithSummary("Cancel a staged restore so it will not apply on the next restart");
        restore.MapGet("/status", Status).WithSummary("Get the current/last restore status");

        return app;
    }

    private static Results<NoContent, Conflict<string>> CancelStaged(PortableRestoreService restoreService)
        => restoreService.CancelStagedRestore()
            ? TypedResults.NoContent()
            : TypedResults.Conflict("No restore is currently staged.");

    private static async Task<Results<Ok<ValidateBackupResponse>, BadRequest<ValidateBackupResponse>>> StageAsync(
        StageRestoreRequest request, PortableRestoreService restoreService, CancellationToken cancellationToken)
    {
        var result = await restoreService.StageAsync(request.FileName, cancellationToken);
        var response = new ValidateBackupResponse
        {
            Success = result.Success,
            Errors = result.Errors,
            BackupId = result.Manifest?.BackupId,
            AppVersion = result.Manifest?.AppVersion,
            SchemaVersion = result.Manifest?.SchemaVersion,
            CreatedUtc = result.Manifest?.CreatedUtc,
        };

        return result.Success ? TypedResults.Ok(response) : TypedResults.BadRequest(response);
    }

    private static async Task<Results<Ok, Conflict<string>>> ConfirmAsync(
        PortableRestoreService restoreService, IApplicationRestartService restartService, CancellationToken cancellationToken)
    {
        var marker = restoreService.GetCurrentStatus();
        if (marker is null || marker.State != RestoreState.Requested)
            return TypedResults.Conflict("No restore is currently staged.");

        await restartService.RequestRestartAsync(cancellationToken);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok<ApplySettingsRestoreResponse>, BadRequest<ApplySettingsRestoreResponse>, NotFound>> ApplySettingsAsync(
        ApplySettingsRestoreRequest request, PortableBackupService backupService, SettingsArchiveService settingsArchiveService,
        CancellationToken cancellationToken)
    {
        var archivePath = backupService.ResolvePath(request.FileName);
        if (archivePath is null)
            return TypedResults.NotFound();

        var result = await settingsArchiveService.ApplyAsync(archivePath, cancellationToken);
        var response = new ApplySettingsRestoreResponse
        {
            Success = result.Success,
            Errors = result.Errors,
            AppliedCounts = result.AppliedCounts,
        };
        return result.Success ? TypedResults.Ok(response) : TypedResults.BadRequest(response);
    }

    private static Ok<RestoreStatusResponse> Status(PortableRestoreService restoreService)
    {
        var marker = restoreService.GetCurrentStatus();
        if (marker is null)
            return TypedResults.Ok(new RestoreStatusResponse { State = nameof(RestoreState.None) });

        return TypedResults.Ok(new RestoreStatusResponse
        {
            State = marker.State.ToString(),
            BackupId = marker.BackupId,
            ArchiveFileName = marker.ArchiveFileName,
            UpdatedUtc = marker.UpdatedUtc,
            ErrorMessage = marker.ErrorMessage,
        });
    }
}
