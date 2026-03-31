using M3Undle.Web.Streaming.Subscribers;

namespace M3Undle.Web.Application;

public sealed record HdHomeRunTunerReservation(
    string ReservationId,
    string VirtualTunerId,
    string StreamKey,
    DateTimeOffset ReservedUtc);

public sealed record HdHomeRunTunerAcquireResult(
    bool Succeeded,
    string? Error,
    HdHomeRunTunerReservation? Reservation,
    SubscriberConnection? PriorSubscriber);

public sealed record HdHomeRunTunerLeaseSnapshot(
    string VirtualTunerId,
    string StreamKey,
    string? ChannelName,
    string? ClientId,
    DateTimeOffset ReservedUtc,
    DateTimeOffset? ActivatedUtc);

public sealed class HdHomeRunTunerManager(
    HdHomeRunTunerCountResolver tunerCountResolver)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TunerLease> _leases = new(StringComparer.Ordinal);

    public HdHomeRunTunerAcquireResult Acquire(string virtualTunerId, string streamKey)
    {
        if (string.IsNullOrWhiteSpace(virtualTunerId))
            virtualTunerId = "hdhr-main";

        lock (_lock)
        {
            _leases.TryGetValue(virtualTunerId, out var priorLease);
            if (priorLease is not null)
                _leases.Remove(virtualTunerId);

            var streamLimit = ResolveStreamLimit();
            if (streamLimit is not null && priorLease is null && _leases.Count >= streamLimit.Value)
            {
                return new HdHomeRunTunerAcquireResult(
                    Succeeded: false,
                    Error: $"All {streamLimit.Value} HDHomeRun tuner slots are in use.",
                    Reservation: null,
                    PriorSubscriber: null);
            }

            var reservation = new HdHomeRunTunerReservation(
                ReservationId: Guid.NewGuid().ToString("N"),
                VirtualTunerId: virtualTunerId,
                StreamKey: streamKey,
                ReservedUtc: DateTimeOffset.UtcNow);

            _leases[virtualTunerId] = new TunerLease(reservation, ChannelName: null, ClientId: null, ActivatedUtc: null, Subscriber: null);

            return new HdHomeRunTunerAcquireResult(
                Succeeded: true,
                Error: null,
                Reservation: reservation,
                PriorSubscriber: priorLease?.Subscriber);
        }
    }

    public HdHomeRunTunerAcquireResult AcquireAuto(string streamKey)
    {
        lock (_lock)
        {
            var tunerCount = tunerCountResolver.ResolveTunerCount();

            if (_leases.Count >= tunerCount)
            {
                return new HdHomeRunTunerAcquireResult(
                    Succeeded: false,
                    Error: $"All {tunerCount} HDHomeRun tuner slots are in use.",
                    Reservation: null,
                    PriorSubscriber: null);
            }

            for (var i = 0; i < tunerCount; i++)
            {
                var tunerId = FormatTunerId(i);
                if (_leases.ContainsKey(tunerId))
                    continue;

                var reservation = new HdHomeRunTunerReservation(
                    ReservationId: Guid.NewGuid().ToString("N"),
                    VirtualTunerId: tunerId,
                    StreamKey: streamKey,
                    ReservedUtc: DateTimeOffset.UtcNow);

                _leases[tunerId] = new TunerLease(reservation, ChannelName: null, ClientId: null, ActivatedUtc: null, Subscriber: null);

                return new HdHomeRunTunerAcquireResult(
                    Succeeded: true,
                    Error: null,
                    Reservation: reservation,
                    PriorSubscriber: null);
            }

            return new HdHomeRunTunerAcquireResult(
                Succeeded: false,
                Error: $"All {tunerCount} HDHomeRun tuner slots are in use.",
                Reservation: null,
                PriorSubscriber: null);
        }
    }

    public bool IsValidTunerIndex(int tunerIndex)
        => tunerIndex >= 0 && tunerIndex < tunerCountResolver.ResolveTunerCount();

    internal static string FormatTunerId(int tunerIndex) => $"tuner{tunerIndex}";

    public void Activate(HdHomeRunTunerReservation reservation, SubscriberConnection subscriber, string? channelName)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(reservation.VirtualTunerId, out var lease) ||
                lease.Reservation.ReservationId != reservation.ReservationId)
            {
                return;
            }

            _leases[reservation.VirtualTunerId] = lease with
            {
                ChannelName = channelName,
                ClientId = subscriber.ClientId,
                ActivatedUtc = DateTimeOffset.UtcNow,
                Subscriber = subscriber,
            };
        }
    }

    public void Release(string reservationId, string? clientId = null)
    {
        lock (_lock)
        {
            var kvp = _leases.FirstOrDefault(x => x.Value.Reservation.ReservationId == reservationId);
            if (string.IsNullOrWhiteSpace(kvp.Key))
                return;

            if (clientId is not null && kvp.Value.ClientId is not null && !string.Equals(kvp.Value.ClientId, clientId, StringComparison.Ordinal))
                return;

            _leases.Remove(kvp.Key);
        }
    }

    public IReadOnlyList<HdHomeRunTunerLeaseSnapshot> GetActiveLeases()
    {
        lock (_lock)
        {
            return _leases.Values
                .Select(x => new HdHomeRunTunerLeaseSnapshot(
                    VirtualTunerId: x.Reservation.VirtualTunerId,
                    StreamKey: x.Reservation.StreamKey,
                    ChannelName: x.ChannelName,
                    ClientId: x.ClientId,
                    ReservedUtc: x.Reservation.ReservedUtc,
                    ActivatedUtc: x.ActivatedUtc))
                .OrderBy(x => x.VirtualTunerId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private int? ResolveStreamLimit() => tunerCountResolver.ResolveStreamLimit();

    private sealed record TunerLease(
        HdHomeRunTunerReservation Reservation,
        string? ChannelName,
        string? ClientId,
        DateTimeOffset? ActivatedUtc,
        SubscriberConnection? Subscriber);
}
