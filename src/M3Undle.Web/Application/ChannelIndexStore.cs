using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace M3Undle.Web.Application;

/// <summary>
/// Reads and writes the per-snapshot channel index in NDJSON + binary-index format.
///
/// Files (in the same snapshot directory):
///   channel_index.ndjson  — one ChannelIndexEntry JSON object per UTF-8 line, natural M3U order
///   channel_index.idx     — sorted binary key index for O(log n) stream-key lookup
///
/// Index file layout (little-endian):
///   [0-3]   uint32  N         — entry count
///   [4-7]   uint32  reserved  — 0
///   N × 24 bytes (sorted ascending by StreamKey):
///     [0-15]  byte[16] streamKey  — ASCII, null-padded to 16 bytes
///     [16-23] uint64   byteOffset — byte position of the corresponding line in .ndjson
///
/// The NDJSON file preserves M3U output order (group-alphabetical then channel-number order).
/// The .idx records are sorted independently so the offset for each record points into the
/// naturally-ordered NDJSON file.
/// </summary>
public static class ChannelIndexStore
{
    private const int HeaderSize = 8;
    private const int RecordSize = 24;
    private const int KeySize    = 16;

    private static readonly byte[] NewlineByte = [(byte)'\n'];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // Per-snapshot cached index bytes (~19 MB for 798 k entries).
    // Invalidated automatically when the snapshot ID changes.
    private static string?           _cachedSnapId;
    private static byte[]?           _cachedIndex;
    private static readonly SemaphoreSlim _idxLock = new(1, 1);

    // -------------------------------------------------------------------------
    // Write (called from SnapshotBuilder)
    // -------------------------------------------------------------------------

    public static async Task WriteAsync(
        string ndjsonPath,
        string idxPath,
        IReadOnlyList<ChannelIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        // Phase 1: write NDJSON in the natural order passed in (M3U output order).
        // Track byte offset of each line so we can build the sorted index.
        var offsets = new long[entries.Count];

        await using var ndjsonFs = new FileStream(
            ndjsonPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 131_072, useAsync: true);

        long pos = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            offsets[i] = pos;
            var lineBytes = JsonSerializer.SerializeToUtf8Bytes(entries[i], JsonOptions);
            await ndjsonFs.WriteAsync(lineBytes, cancellationToken);
            await ndjsonFs.WriteAsync(NewlineByte, cancellationToken);
            pos += lineBytes.Length + 1;
        }

        // Phase 2: write sorted binary index.
        // Build (streamKey, offset) pairs sorted by key so binary search works.
        var indexRecords = entries
            .Select((e, i) => (Key: e.StreamKey, Offset: offsets[i]))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();

        await using var idxFs = new FileStream(
            idxPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 65_536, useAsync: true);

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)indexRecords.Length);
        await idxFs.WriteAsync(header, cancellationToken);

        var record = new byte[RecordSize];
        foreach (var (key, offset) in indexRecords)
        {
            Array.Clear(record, 0, RecordSize);
            var keyBytes = Encoding.ASCII.GetBytes(key);
            keyBytes.AsSpan(0, Math.Min(keyBytes.Length, KeySize)).CopyTo(record);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(KeySize), (ulong)offset);
            await idxFs.WriteAsync(record, cancellationToken);
        }
    }

    // -------------------------------------------------------------------------
    // Stream key lookup — binary search in the in-memory index, single seek into NDJSON
    // -------------------------------------------------------------------------

    public static async Task<ChannelIndexEntry?> TryLookupAsync(
        string snapId,
        string ndjsonPath,
        string idxPath,
        string streamKey,
        CancellationToken cancellationToken)
    {
        var index = await GetOrLoadIndexAsync(snapId, idxPath, cancellationToken);
        if (index is null || index.Length < HeaderSize) return null;

        var keyBuf = new byte[KeySize];
        var encoded = Encoding.ASCII.GetBytes(streamKey);
        encoded.AsSpan(0, Math.Min(encoded.Length, KeySize)).CopyTo(keyBuf);

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(index);
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            var recordKey = index.AsSpan(HeaderSize + mid * RecordSize, KeySize);
            int cmp = recordKey.SequenceCompareTo(keyBuf);
            if (cmp == 0)
            {
                var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(
                    index.AsSpan(HeaderSize + mid * RecordSize + KeySize));
                return await ReadLineAtAsync(ndjsonPath, offset, cancellationToken);
            }
            if (cmp < 0) lo = mid + 1;
            else         hi = mid - 1;
        }
        return null;
    }

    // -------------------------------------------------------------------------
    // Stream all entries in natural order (for M3U generation, group scans, etc.)
    // -------------------------------------------------------------------------

    public static async IAsyncEnumerable<ChannelIndexEntry> StreamAllAsync(
        string ndjsonPath,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var fs = new FileStream(
            ndjsonPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 131_072, useAsync: true);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length == 0) continue;
            var entry = JsonSerializer.Deserialize<ChannelIndexEntry>(line, JsonOptions);
            if (entry is not null) yield return entry;
        }
    }

    // -------------------------------------------------------------------------
    // Path helpers
    // -------------------------------------------------------------------------

    public static string GetIdxPath(string ndjsonPath)
        => Path.ChangeExtension(ndjsonPath, ".idx");

    // -------------------------------------------------------------------------
    // Private
    // -------------------------------------------------------------------------

    private static async Task<byte[]?> GetOrLoadIndexAsync(
        string snapId,
        string idxPath,
        CancellationToken cancellationToken)
    {
        if (_cachedSnapId == snapId && _cachedIndex is { } warm) return warm;

        await _idxLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedSnapId == snapId && _cachedIndex is { } cached) return cached;
            if (!File.Exists(idxPath)) return null;

            // CancellationToken.None: load the full index so the cache is warm for
            // the next request even if this request is cancelled mid-load.
            var bytes = await File.ReadAllBytesAsync(idxPath, CancellationToken.None);
            if (bytes.Length < HeaderSize) return null;

            _cachedIndex  = bytes;
            _cachedSnapId = snapId;
            return bytes;
        }
        finally
        {
            _idxLock.Release();
        }
    }

    private static async Task<ChannelIndexEntry?> ReadLineAtAsync(
        string ndjsonPath, long offset, CancellationToken cancellationToken)
    {
        await using var fs = new FileStream(
            ndjsonPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        fs.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var line = await reader.ReadLineAsync(cancellationToken);
        if (line is null) return null;
        return JsonSerializer.Deserialize<ChannelIndexEntry>(line, JsonOptions);
    }
}
