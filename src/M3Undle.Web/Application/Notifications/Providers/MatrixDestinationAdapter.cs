using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Notifications.Providers;

public sealed class MatrixDestinationAdapter(SecretEncryptionService encryption) : INotificationDestinationAdapter
{
    public const string RoomTargetId = "matrix-room";

    public string Kind => NotificationProviderKinds.Matrix;

    public bool IsStructurallyComplete(NotificationDestination destination)
    {
        var m = destination.Matrix;
        return m is not null
            && !string.IsNullOrWhiteSpace(m.HomeserverUrl)
            && !string.IsNullOrWhiteSpace(m.RoomId)
            && !string.IsNullOrEmpty(m.AccessTokenEncrypted);
    }

    public IReadOnlyList<NotificationTarget> GetTargets(NotificationDestination destination) =>
        [new NotificationTarget(RoomTargetId, "Matrix room")];

    public NotificationProviderConfiguration? ResolveConfiguration(NotificationDestination destination)
    {
        var m = destination.Matrix;
        if (!IsStructurallyComplete(destination) || m is null)
            return null;

        try
        {
            return new MatrixProviderConfiguration(
                m.HomeserverUrl!, m.RoomId!, new NotificationSecret(encryption.Decrypt(m.AccessTokenEncrypted!)),
                m.BotUserId, m.DeviceId, m.AllowInsecureHttp);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public bool ApplyDiscovered(NotificationDestination destination, IReadOnlyDictionary<string, string> discovered)
    {
        var m = destination.Matrix;
        if (m is null)
            return false;

        var changed = false;
        if (discovered.TryGetValue("user_id", out var userId) && m.BotUserId != userId)
        {
            m.BotUserId = userId;
            changed = true;
        }

        if (discovered.TryGetValue("device_id", out var deviceId) && m.DeviceId != deviceId)
        {
            // A different device is a different transaction-ID scope, so it is a different delivery identity.
            if (m.DeviceId is not null)
                destination.DeliveryIdentityRevision++;
            m.DeviceId = deviceId;
            changed = true;
        }

        return changed;
    }
}
