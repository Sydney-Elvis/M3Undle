using System.Security.Cryptography;
using System.Text;

namespace M3Undle.Web.Application;

/// <summary>
/// Singleton that maintains a per-snapshot mapping between Xtream numeric stream IDs
/// (a stable 23-bit value derived from MD5(streamKey)) and the actual stream keys used by M3Undle's
/// channel index. Invalidated automatically when the active snapshot changes.
/// </summary>
public sealed class XtreamStreamIdCache(ILogger<XtreamStreamIdCache> logger)
{
    private string? _cachedSnapshotId;
    private Dictionary<int, string>? _idToKey;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Computes a stable 23-bit numeric stream ID from a stream key using MD5.
    /// Capped at 23 bits (max 8,388,607, always below 10M) so Brightscript renders it
    /// as plain digits rather than scientific notation when building stream URLs.
    /// </summary>
    public static int ToStreamId(string streamKey)
    {
        var bytes = Encoding.UTF8.GetBytes(streamKey);
        var hash = MD5.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0);
        // Mask to 23 bits (max 8,388,607) so the ID is always below 10,000,000.
        // Brightscript renders floats >= 1e7 as scientific notation (e.g. "1.618042e+07"),
        // which breaks URL construction. Keeping IDs < 10M ensures plain-integer formatting.
        return (int)(value & 0x007F_FFFF);
    }

    /// <summary>
    /// Looks up the stream key for a given numeric Xtream stream ID.
    /// Returns <c>null</c> if the ID is not present in the current snapshot.
    /// </summary>
    public async Task<string?> TryGetStreamKeyAsync(
        string snapshotId,
        string ndjsonPath,
        int streamId,
        CancellationToken cancellationToken)
    {
        var map = await GetOrBuildMapAsync(snapshotId, ndjsonPath, cancellationToken);
        return map.TryGetValue(streamId, out var key) ? key : null;
    }

    private async Task<Dictionary<int, string>> GetOrBuildMapAsync(
        string snapshotId,
        string ndjsonPath,
        CancellationToken cancellationToken)
    {
        if (_cachedSnapshotId == snapshotId && _idToKey is { } warm)
            return warm;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSnapshotId == snapshotId && _idToKey is { } cached)
                return cached;

            var map = new Dictionary<int, string>();
            await foreach (var entry in ChannelIndexStore.StreamAllAsync(ndjsonPath, cancellationToken))
            {
                var id = ToStreamId(entry.StreamKey);
                if (!map.TryAdd(id, entry.StreamKey))
                {
                    logger.LogWarning(
                        "Xtream stream ID collision detected: id={StreamId} existingStreamKey={ExistingStreamKey} droppedStreamKey={DroppedStreamKey}.",
                        id,
                        map[id],
                        entry.StreamKey);
                }
            }

            _idToKey = map;
            _cachedSnapshotId = snapshotId;
            return map;
        }
        finally
        {
            _lock.Release();
        }
    }
}
