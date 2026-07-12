using M3Undle.Web.Application;
using M3Undle.Web.Contracts;
using M3Undle.Web.Security;
using Microsoft.AspNetCore.Http.HttpResults;

namespace M3Undle.Web.Api;

public static class EncryptionApiEndpoints
{
    public static IEndpointRouteBuilder MapEncryptionApiEndpoints(this IEndpointRouteBuilder app)
    {
        var encryption = app.MapGroup("/api/v1/encryption");
        encryption.RequireAuthorization(UiAccessPolicy.Name);
        encryption.WithTags("Encryption");
        encryption.MapGet("/status", GetStatusAsync).WithSummary("Get encryption key rotation status");
        encryption.MapPost("/rotate", RotateAsync).WithSummary("Rotate encrypted columns to the active key");

        return app;
    }

    private static async Task<Ok<EncryptionStatusResponse>> GetStatusAsync(
        EncryptionRotationService rotation,
        CancellationToken cancellationToken)
    {
        var result = await rotation.GetStatusAsync(cancellationToken);
        return TypedResults.Ok(new EncryptionStatusResponse
        {
            EncryptionAvailable = result.EncryptionAvailable,
            ActiveKeyId = result.ActiveKeyId,
            KnownKeyIds = result.KnownKeyIds,
            ProvidersOnActiveKey = result.ProvidersOnActiveKey,
            ProvidersOnOtherKey = result.ProvidersOnOtherKey,
            DownstreamIntegrationsOnActiveKey = result.DownstreamIntegrationsOnActiveKey,
            DownstreamIntegrationsOnOtherKey = result.DownstreamIntegrationsOnOtherKey,
        });
    }

    private static async Task<Results<Ok<RotateEncryptionResponse>, Conflict<string>>> RotateAsync(
        EncryptionRotationService rotation,
        CancellationToken cancellationToken)
    {
        var result = await rotation.RotateAsync(cancellationToken);
        if (!result.Success)
            return TypedResults.Conflict(result.ErrorMessage ?? "Rotation failed.");

        return TypedResults.Ok(new RotateEncryptionResponse
        {
            ActiveKeyId = result.ActiveKeyId,
            BackupFileName = result.BackupFilePath is null ? null : Path.GetFileName(result.BackupFilePath),
            ProvidersMigrated = result.ProvidersMigrated,
            DownstreamIntegrationsMigrated = result.DownstreamIntegrationsMigrated,
            RowsAlreadyCurrent = result.RowsAlreadyCurrent,
        });
    }
}
