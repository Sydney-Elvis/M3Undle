using System.Security.Cryptography;
using System.Text;

namespace M3Undle.Web.Application;

/// <summary>
/// Singleton that maintains a per-snapshot mapping between Xtream numeric stream IDs
/// (a stable CRC32 of the streamKey) and the actual stream keys used by M3Undle's
/// channel index. Invalidated automatically when the active snapshot changes.
/// </summary>
public sealed class XtreamStreamIdCache
{
    private string? _cachedSnapshotId;
    private Dictionary<int, string>? _idToKey;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Computes a stable 31-bit numeric stream ID from a stream key using CRC32.
    /// The sign bit is masked off so the value is always a positive <see cref="int"/>.
    /// </summary>
    public static int ToStreamId(string streamKey)
    {
        var bytes = Encoding.UTF8.GetBytes(streamKey);
        var hash = MD5.HashData(bytes);
        var value = BitConverter.ToUInt32(hash, 0);
        return (int)(value & 0x7FFF_FFFF);
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
                map.TryAdd(id, entry.StreamKey); // on collision, first entry wins
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
