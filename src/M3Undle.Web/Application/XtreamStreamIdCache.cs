using System.Security.Cryptography;
using System.Text;

namespace M3Undle.Web.Application;

/// <summary>
/// Singleton that maintains a per-snapshot <b>bijective</b> mapping between Xtream numeric
/// stream IDs and the stream keys used by M3Undle's channel index. Invalidated automatically
/// when the active snapshot changes.
///
/// <para>
/// Stream IDs must satisfy two independent client constraints <b>at the same time</b>:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Android TV / NextPVR (standard Xtream clients):</b> every ID advertised by
///     <c>player_api</c> must resolve back to its own channel. The ID↔key mapping has to be a
///     collision-free bijection, otherwise some channels become unreachable (their ID maps to a
///     different channel) — which is exactly what tanked NextPVR playback.
///   </item>
///   <item>
///     <b>Roku / Brightscript:</b> IDs are handled as 32-bit floats and render in scientific
///     notation once they reach 1e7 (e.g. <c>"8.150113e+08"</c>), corrupting the stream URL.
///     IDs must therefore stay below 10,000,000 so they always format as plain digits.
///   </item>
/// </list>
///
/// <para>
/// A raw MD5 hash satisfies neither constraint alone: a 31-bit hash exceeds 1e7 (breaks Roku),
/// while masking it below 1e7 collides for large lineups (breaks Android TV). We resolve both by
/// hashing into the sub-1e7 space and then linear-probing to the next free slot, yielding a
/// mapping that is collision-free <i>and</i> Brightscript-safe.
/// </para>
/// </summary>
public sealed class XtreamStreamIdCache(ILogger<XtreamStreamIdCache> logger)
{
    /// <summary>
    /// Largest stream ID we will ever emit — one below 10,000,000 — so every ID renders as a
    /// plain integer in Brightscript rather than scientific notation. Also the size of the
    /// addressable ID space ([1, <see cref="MaxStreamId"/>]).
    /// </summary>
    public const int MaxStreamId = 9_999_999;

    private readonly object _gate = new();
    private volatile CachedAssignment? _cache;

    private sealed record CachedAssignment(string SnapshotId, StreamIdAssignment Assignment);

    /// <summary>
    /// Computes the <i>preferred</i> (pre-collision-resolution) stream ID for a key: a stable
    /// value in the range [1, <see cref="MaxStreamId"/>] derived from MD5(streamKey). The final
    /// assigned ID can differ if this slot is already taken — see <see cref="BuildAssignment"/>.
    /// </summary>
    public static int ToStreamId(string streamKey)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(streamKey));
        var value = BitConverter.ToUInt32(hash, 0);
        // Map into [1, MaxStreamId] so the ID is always >= 1 and < 10,000,000.
        return (int)(value % MaxStreamId) + 1;
    }

    /// <summary>
    /// Computes the legacy 31-bit stream ID a key had before IDs were capped below 10,000,000.
    /// Used only for <b>backward-compatible resolution</b>: clients (NextPVR, Jellyfin, Smarters)
    /// cache their channel list and keep requesting these old IDs until they re-pull. We never
    /// advertise them again, but we still resolve them so cached clients keep working.
    /// </summary>
    public static int ToLegacyStreamId(string streamKey)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(streamKey));
        var value = BitConverter.ToUInt32(hash, 0);
        return (int)(value & 0x7FFF_FFFF);
    }

    /// <summary>
    /// Builds a collision-free, Brightscript-safe bijection between stream keys and numeric IDs.
    /// Each key prefers <see cref="ToStreamId"/>; on a clash the next free slot (wrapping within
    /// [1, <see cref="MaxStreamId"/>]) is taken. Keys are processed in a deterministic order so the
    /// assignment is reproducible for a given set of keys.
    ///
    /// <para>
    /// A secondary <see cref="StreamIdAssignment.LegacyIdToKey"/> map is also built so that IDs
    /// cached by clients before the cap (which are &gt; <see cref="MaxStreamId"/>) still resolve.
    /// Only legacy IDs above the cap are bridged — they can never collide with a current ID, so the
    /// bridge is unambiguous — while current IDs always take precedence.
    /// </para>
    /// </summary>
    public static StreamIdAssignment BuildAssignment(IEnumerable<string> streamKeys)
    {
        var idToKey = new Dictionary<int, string>();
        var keyToId = new Dictionary<string, int>(StringComparer.Ordinal);
        var legacyIdToKey = new Dictionary<int, string>();

        foreach (var key in streamKeys
                     .Where(static k => !string.IsNullOrEmpty(k))
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(static k => k, StringComparer.Ordinal))
        {
            var id = ToStreamId(key);
            var probes = 0;
            while (!idToKey.TryAdd(id, key))
            {
                id = id == MaxStreamId ? 1 : id + 1;
                if (++probes > MaxStreamId)
                    throw new InvalidOperationException(
                        "Xtream stream ID space exhausted; the lineup exceeds the addressable ID range.");
            }

            keyToId[key] = id;

            // Bridge only legacy IDs above the cap: they are disjoint from current IDs, so there is
            // no ambiguity. (Legacy IDs that already fell below the cap are indistinguishable from
            // current IDs and are intentionally not bridged.)
            var legacyId = ToLegacyStreamId(key);
            if (legacyId > MaxStreamId)
                legacyIdToKey.TryAdd(legacyId, key);
        }

        return new StreamIdAssignment(idToKey, keyToId, legacyIdToKey);
    }

    /// <summary>
    /// Returns the (cached) bijective ID assignment for a snapshot, built from the rendered
    /// lineup's stream keys. Both the <c>player_api</c> listing (key → ID) and path-auth streaming
    /// (ID → key) call this with the same snapshot lineup, so the IDs they advertise and resolve
    /// always agree. The assignment is rebuilt only when the active snapshot changes.
    /// </summary>
    public StreamIdAssignment GetAssignment(string snapshotId, IEnumerable<string> streamKeys)
    {
        var snapshot = _cache;
        if (snapshot is not null && snapshot.SnapshotId == snapshotId)
            return snapshot.Assignment;

        lock (_gate)
        {
            snapshot = _cache;
            if (snapshot is not null && snapshot.SnapshotId == snapshotId)
                return snapshot.Assignment;

            var assignment = BuildAssignment(streamKeys);

            var relocated = assignment.KeyToId.Count(kv => ToStreamId(kv.Key) != kv.Value);
            logger.LogInformation(
                "Xtream stream ID assignment built for snapshot {SnapshotId}: {Channels} channels, {Relocated} relocated to keep IDs unique and below {Max}.",
                snapshotId,
                assignment.KeyToId.Count,
                relocated,
                MaxStreamId);

            _cache = new CachedAssignment(snapshotId, assignment);
            return assignment;
        }
    }

    /// <summary>
    /// Looks up the stream key for a given numeric Xtream stream ID within the snapshot lineup.
    /// Current IDs resolve first; a legacy (pre-cap) ID still cached by a client resolves via the
    /// backward-compatibility bridge. Returns <c>null</c> if the ID is not present.
    /// </summary>
    public string? TryGetStreamKey(string snapshotId, IEnumerable<string> streamKeys, int streamId)
    {
        var assignment = GetAssignment(snapshotId, streamKeys);
        return assignment.IdToKey.GetValueOrDefault(streamId)
            ?? assignment.LegacyIdToKey.GetValueOrDefault(streamId);
    }
}

/// <summary>
/// A collision-free, Brightscript-safe bijection between channel stream keys and numeric Xtream
/// stream IDs for a single snapshot. <paramref name="LegacyIdToKey"/> additionally maps pre-cap
/// (&gt; <see cref="XtreamStreamIdCache.MaxStreamId"/>) IDs back to their keys so clients that
/// cached the old IDs keep resolving until they re-pull; it is never used for advertisement.
/// </summary>
public sealed record StreamIdAssignment(
    IReadOnlyDictionary<int, string> IdToKey,
    IReadOnlyDictionary<string, int> KeyToId,
    IReadOnlyDictionary<int, string> LegacyIdToKey);
