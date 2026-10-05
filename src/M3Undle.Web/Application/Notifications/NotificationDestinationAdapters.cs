using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application.Notifications;

/// <summary>
/// The provider-specific half of a destination that routing and the worker need: whether the saved setup is complete,
/// which targets it addresses, and the typed configuration with secrets decrypted. Adding a provider means registering
/// one of these next to its <see cref="INotificationProvider"/>; routing, incidents and the worker never switch on kind.
/// </summary>
public interface INotificationDestinationAdapter
{
    string Kind { get; }

    bool IsStructurallyComplete(NotificationDestination destination);

    IReadOnlyList<NotificationTarget> GetTargets(NotificationDestination destination);

    /// <summary>Null when the configuration cannot be used, for example a secret that no longer decrypts.</summary>
    NotificationProviderConfiguration? ResolveConfiguration(NotificationDestination destination);

    /// <summary>
    /// Stores values a connection check discovered (for example a Matrix device). It must not change the delivery identity
    /// unless the discovered value genuinely differs from what was stored; returns true when it changed anything.
    /// </summary>
    bool ApplyDiscovered(NotificationDestination destination, IReadOnlyDictionary<string, string> discovered) => false;
}

public sealed class NotificationDestinationAdapters
{
    private readonly Dictionary<string, INotificationDestinationAdapter> _adapters;

    public NotificationDestinationAdapters(IEnumerable<INotificationDestinationAdapter> adapters)
    {
        _adapters = new Dictionary<string, INotificationDestinationAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            if (!_adapters.TryAdd(adapter.Kind, adapter))
                throw new InvalidOperationException($"Notification destination adapter '{adapter.Kind}' is registered more than once.");
        }
    }

    public INotificationDestinationAdapter? Find(string kind) => _adapters.GetValueOrDefault(kind);
}
