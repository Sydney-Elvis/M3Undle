using M3Undle.Web.Streaming.Subscribers;
using Microsoft.Extensions.Options;

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
    IOptions<HdHomeRunOptions> options,
    EnvironmentVariableService env)
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

            var tunerCount = ResolveTunerCount();
            if (priorLease is null && _leases.Count >= tunerCount)
            {
                return new HdHomeRunTunerAcquireResult(
                    Succeeded: false,
                    Error: $"All {tunerCount} HDHomeRun tuner slots are in use.",
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

    private int ResolveTunerCount()
    {
        var envValue = env.GetValue("M3UNDLE_HDHR_TUNER_COUNT");
        if (int.TryParse(envValue, out var parsed))
            return Math.Clamp(parsed, 1, 32);

        return Math.Clamp(options.Value.TunerCount, 1, 32);
    }

    private sealed record TunerLease(
        HdHomeRunTunerReservation Reservation,
        string? ChannelName,
        string? ClientId,
        DateTimeOffset? ActivatedUtc,
        SubscriberConnection? Subscriber);
}
