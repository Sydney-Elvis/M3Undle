using M3Undle.Web.Application.Backup;
using M3Undle.Web.Contracts;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace M3Undle.Web.Api;

public static class BackupApiEndpoints
{
    private const long MaxUploadBytes = 500L * 1024 * 1024;

    public static IEndpointRouteBuilder MapBackupApiEndpoints(this IEndpointRouteBuilder app)
    {
        var backups = app.MapGroup("/api/v1/backups");
        backups.RequireAuthorization(UiAccessPolicy.Name);
        backups.WithTags("Backups");

        backups.MapGet("/", ListAsync).WithSummary("List portable backups");
        backups.MapPost("/", CreateAsync).WithSummary("Create a portable backup now");
        backups.MapGet("/{fileName}/download", DownloadAsync).WithSummary("Download a portable backup archive");
        backups.MapPost("/{fileName}/validate", ValidateAsync).WithSummary("Validate a portable backup archive");
        backups.MapDelete("/{fileName}", Delete).WithSummary("Delete a portable backup");

        backups.MapPost("/upload", UploadAsync)
            .WithSummary("Upload a portable backup archive for later restore")
            .DisableAntiforgery()
            .WithMetadata(new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxUploadBytes });

        backups.MapGet("/schedule", GetScheduleAsync).WithSummary("Get the weekly backup schedule");
        backups.MapPut("/schedule", SetScheduleAsync).WithSummary("Enable or disable the weekly backup schedule");

        return app;
    }

    private static Ok<IReadOnlyList<BackupSummaryResponse>> ListAsync(PortableBackupService backupService)
    {
        var summaries = backupService.List()
            .Select(s => new BackupSummaryResponse { FileName = s.FileName, SizeBytes = s.SizeBytes, CreatedUtc = s.CreatedUtc })
            .ToList();

        return TypedResults.Ok<IReadOnlyList<BackupSummaryResponse>>(summaries);
    }

    private static async Task<Results<Ok<CreateBackupResponse>, Conflict<string>>> CreateAsync(
        PortableBackupService backupService, CancellationToken cancellationToken)
    {
        var result = await backupService.CreateAsync(cancellationToken);
        if (!result.Success)
            return TypedResults.Conflict(result.ErrorMessage ?? "Backup creation failed.");

        return TypedResults.Ok(new CreateBackupResponse
        {
            FileName = Path.GetFileName(result.FilePath!),
            SizeBytes = result.Report!.DatabaseSizeBytes,
            AppVersion = result.Manifest!.AppVersion,
            SchemaVersion = result.Manifest.SchemaVersion,
            RowsRemovedByTable = result.Report.RowsRemovedByTable,
            DurationSeconds = result.Report.DurationSeconds,
        });
    }

    private static Results<PhysicalFileHttpResult, NotFound> DownloadAsync(string fileName, PortableBackupService backupService)
    {
        var path = backupService.ResolvePath(fileName);
        if (path is null)
            return TypedResults.NotFound();

        return TypedResults.PhysicalFile(path, "application/octet-stream", Path.GetFileName(path));
    }

    private static async Task<Results<Ok<ValidateBackupResponse>, NotFound>> ValidateAsync(
        string fileName, PortableBackupService backupService, PortableRestoreService restoreService, CancellationToken cancellationToken)
    {
        var path = backupService.ResolvePath(fileName);
        if (path is null)
            return TypedResults.NotFound();

        var preflight = await restoreService.PreflightAsync(path, cancellationToken);
        return TypedResults.Ok(new ValidateBackupResponse
        {
            Success = preflight.Success,
            Errors = preflight.Errors,
            BackupId = preflight.Manifest?.BackupId,
            AppVersion = preflight.Manifest?.AppVersion,
            SchemaVersion = preflight.Manifest?.SchemaVersion,
            CreatedUtc = preflight.Manifest?.CreatedUtc,
        });
    }

    private static Results<NoContent, NotFound> Delete(string fileName, PortableBackupService backupService)
        => backupService.Delete(fileName) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static async Task<Results<Ok<BackupSummaryResponse>, BadRequest<string>>> UploadAsync(
        IFormFile file, PortableBackupService backupService, CancellationToken cancellationToken)
    {
        if (file.Length == 0)
            return TypedResults.BadRequest("Uploaded file is empty.");

        await using var stream = file.OpenReadStream();
        var savedFileName = await backupService.SaveUploadedArchiveAsync(stream, cancellationToken);
        var summary = backupService.List().First(s => s.FileName == savedFileName);

        return TypedResults.Ok(new BackupSummaryResponse { FileName = summary.FileName, SizeBytes = summary.SizeBytes, CreatedUtc = summary.CreatedUtc });
    }

    private static async Task<Ok<BackupScheduleResponse>> GetScheduleAsync(IBackupScheduleService scheduleService, CancellationToken cancellationToken)
    {
        var settings = await scheduleService.GetSettingsAsync(cancellationToken);
        var next = await scheduleService.GetNextScheduledBackupUtcAsync(cancellationToken);
        return TypedResults.Ok(new BackupScheduleResponse { Enabled = settings.Enabled, LastRunUtc = settings.LastRunUtc, NextRunUtc = next });
    }

    private static async Task<Ok<BackupScheduleResponse>> SetScheduleAsync(
        SetBackupScheduleRequest request, IBackupScheduleService scheduleService, CancellationToken cancellationToken)
    {
        await scheduleService.SetEnabledAsync(request.Enabled, cancellationToken);
        var settings = await scheduleService.GetSettingsAsync(cancellationToken);
        var next = await scheduleService.GetNextScheduledBackupUtcAsync(cancellationToken);
        return TypedResults.Ok(new BackupScheduleResponse { Enabled = settings.Enabled, LastRunUtc = settings.LastRunUtc, NextRunUtc = next });
    }
}
