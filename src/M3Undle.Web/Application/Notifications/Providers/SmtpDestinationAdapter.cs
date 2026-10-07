using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Notifications.Providers;

public sealed class SmtpDestinationAdapter(SecretEncryptionService encryption) : INotificationDestinationAdapter
{
    public string Kind => NotificationProviderKinds.Smtp;

    public bool IsStructurallyComplete(NotificationDestination destination)
    {
        var s = destination.Smtp;
        if (s is null || string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.SenderAddress) || destination.Recipients.Count == 0)
            return false;

        return s.AuthMode != "password" || (!string.IsNullOrEmpty(s.Username) && !string.IsNullOrEmpty(s.PasswordEncrypted));
    }

    public IReadOnlyList<NotificationTarget> GetTargets(NotificationDestination destination) =>
        destination.Recipients
            .OrderBy(r => r.SortOrder)
            .Select(r => new NotificationTarget(r.RecipientId, r.Address))
            .ToList();

    public NotificationProviderConfiguration? ResolveConfiguration(NotificationDestination destination)
    {
        var s = destination.Smtp;
        if (!IsStructurallyComplete(destination) || s is null)
            return null;

        NotificationSecret? password = null;
        if (s.AuthMode == "password")
        {
            try
            {
                password = new NotificationSecret(encryption.Decrypt(s.PasswordEncrypted!));
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        return new SmtpProviderConfiguration(s.Host!, s.Port, s.TlsMode, s.AuthMode, s.Username, password, s.SenderAddress!, s.SenderName);
    }
}
