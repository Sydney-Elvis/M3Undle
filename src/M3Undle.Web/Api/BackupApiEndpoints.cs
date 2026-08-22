using M3Undle.Web.Application.Backup;
using M3Undle.Web.Contracts;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace M3Undle.Web.Api;

public static class BackupApiEndpoints
{
    /// <summary>Rate-limit policy applied to backup/restore initiation endpoints — defined in Program.cs.</summary>
    public const string RateLimitPolicyName = "backup-restore";

    public static IEndpointRouteBuilder MapBackupApiEndpoints(this IEndpointRouteBuilder app)
    {
        var backups = app.MapGroup("/api/v1/backups");
        backups.RequireAuthorization(UiAccessPolicy.Name);
        backups.WithTags("Backups");

        backups.MapGet("/", ListAsync).WithSummary("List portable backups");
        backups.MapPost("/", CreateAsync).WithSummary("Create a portable backup now")
            .RequireRateLimiting(RateLimitPolicyName);
        backups.MapGet("/{fileName}/download", DownloadAsync).WithSummary("Download a portable backup archive");
        backups.MapPost("/{fileName}/validate", ValidateAsync).WithSummary("Validate a portable backup archive")
            .RequireRateLimiting(RateLimitPolicyName);
        backups.MapDelete("/{fileName}", Delete).WithSummary("Delete a portable backup");

        backups.MapPost("/upload", UploadAsync)
            .WithSummary("Upload a portable backup archive for later restore. Requires an X-Requested-With header.")
            .RequireRateLimiting(RateLimitPolicyName);

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

    private static async Task<Results<Ok<CreateBackupResponse>, Conflict<string>, BadRequest<string>>> CreateAsync(
        string? scope, CreateBackupRequest? request, PortableBackupService backupService,
        SettingsArchiveService settingsArchiveService, CancellationToken cancellationToken)
    {
        if (string.Equals(scope, "settings", StringComparison.OrdinalIgnoreCase))
        {
            var settingsResult = await settingsArchiveService.CreateAsync(request?.Passphrase ?? string.Empty, cancellationToken);
            if (!settingsResult.Success)
                return TypedResults.Conflict(settingsResult.ErrorMessage ?? "Settings archive creation failed.");

            return TypedResults.Ok(new CreateBackupResponse
            {
                FileName = Path.GetFileName(settingsResult.FilePath!),
                SizeBytes = new FileInfo(settingsResult.FilePath!).Length,
                AppVersion = settingsResult.Manifest!.AppVersion,
                SchemaVersion = settingsResult.Manifest.SchemaVersion,
                Scope = "settings",
                SettingsEntities = settingsResult.Manifest.SettingsEntities,
            });
        }

        if (!string.IsNullOrWhiteSpace(scope) && !string.Equals(scope, "full", StringComparison.OrdinalIgnoreCase))
            return TypedResults.BadRequest("scope must be 'full' or 'settings'.");

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
            Scope = "full",
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
        string fileName, PortableBackupService backupService, PortableRestoreService restoreService,
        SettingsArchiveService settingsArchiveService, CancellationToken cancellationToken)
    {
        var path = backupService.ResolvePath(fileName);
        if (path is null)
            return TypedResults.NotFound();

        var settingsPreflight = await settingsArchiveService.PreflightAsync(path, null, cancellationToken);
        if (settingsPreflight.IsSettingsArchive)
        {
            return TypedResults.Ok(new ValidateBackupResponse
            {
                Success = false,
                Errors = settingsPreflight.Errors,
            });
        }

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

    // Binds HttpContext instead of IFormFile: form binding would read the body before the
    // per-request size limit could be raised (Kestrel's ~30 MB default is below the archive
    // cap), and would also require antiforgery tokens that non-browser API clients can't
    // produce. CSRF is covered by requiring a custom header instead — cross-site form posts
    // can't set one, while curl/scripts trivially can.
    private static async Task<Results<Ok<UploadBackupResponse>, BadRequest<string>>> UploadAsync(
        HttpContext context, PortableBackupService backupService, PortableRestoreService restoreService,
        SettingsArchiveService settingsArchiveService, CancellationToken cancellationToken)
    {
        if (!context.Request.Headers.ContainsKey("X-Requested-With"))
            return TypedResults.BadRequest("Missing required X-Requested-With header.");

        var sizeLimitFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (sizeLimitFeature is { IsReadOnly: false })
            sizeLimitFeature.MaxRequestBodySize = PortableBackupFormat.MaxArchiveSizeBytes + 1024 * 1024;

        if (!context.Request.HasFormContentType)
            return TypedResults.BadRequest("Expected a multipart form upload.");

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files.Count == 1 ? form.Files[0] : null;
        if (file is null || file.Length == 0)
            return TypedResults.BadRequest("Expected exactly one non-empty file.");

        string savedFileName;
        await using (var stream = file.OpenReadStream())
        {
            try
            {
                savedFileName = await backupService.SaveUploadedArchiveAsync(stream, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        }

        // Validate immediately so a corrupt or incompatible upload is flagged now, not at
        // restore time. The file is kept either way — the caller decides whether to delete it.
        var archivePath = backupService.ResolvePath(savedFileName)!;
        var settingsPreflight = await settingsArchiveService.PreflightAsync(archivePath, null, cancellationToken);
        var preflight = settingsPreflight.IsSettingsArchive
            ? (Success: settingsPreflight.Success, Errors: settingsPreflight.Errors)
            : await ReadFullPreflightAsync(restoreService, archivePath, cancellationToken);
        var summary = backupService.List().First(s => s.FileName == savedFileName);

        return TypedResults.Ok(new UploadBackupResponse
        {
            FileName = summary.FileName,
            SizeBytes = summary.SizeBytes,
            CreatedUtc = summary.CreatedUtc,
            Valid = preflight.Success,
            ValidationErrors = preflight.Errors,
        });
    }

    private static async Task<(bool Success, IReadOnlyList<string> Errors)> ReadFullPreflightAsync(
        PortableRestoreService restoreService, string archivePath, CancellationToken cancellationToken)
    {
        var preflight = await restoreService.PreflightAsync(archivePath, cancellationToken);
        return (preflight.Success, preflight.Errors);
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
