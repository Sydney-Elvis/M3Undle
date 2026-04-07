using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using M3Undle.Web.Data.Entities;

namespace M3Undle.Web.Application;

/// <summary>
/// Compares a newly built snapshot against the previous active snapshot and returns a
/// ChangeClass string indicating what kind of change occurred.
///
/// Change classes (in ascending severity):
///   none       — same channels, same guide content
///   guide_only — same channels, guide content changed
///   lineup     — channels added/removed (≤ 20 % churn)
///   breaking   — > 20 % channel identity churn; clients may need to rescan
///   null       — no previous snapshot to compare against (first-ever run)
/// </summary>
public static class SnapshotChangeClassifier
{
    private const double BreakingChurnThreshold = 0.20;

    // idx file constants (matching ChannelIndexStore)
    private const int HeaderSize = 8;
    private const int RecordSize = 24;
    private const int KeySize    = 16;

    /// <summary>
    /// Classify the change between <paramref name="prev"/> and the newly-built snapshot.
    /// Returns null if <paramref name="prev"/> is null (no previous state).
    /// </summary>
    public static async Task<string?> ClassifyAsync(
        Snapshot? prev,
        string newIdxPath,
        string newXmltvPath,
        CancellationToken ct)
    {
        if (prev is null)
            return null;

        if (!File.Exists(prev.ChannelIndexPath) || !File.Exists(newIdxPath))
            return null; // Can't compare — treat as unknown, caller will fire all notifications

        var prevKeys = await ReadKeysAsync(prev.ChannelIndexPath.Replace(".ndjson", ".idx"), ct);
        var newKeys  = await ReadKeysAsync(newIdxPath.Replace(".ndjson", ".idx"), ct);

        int added   = newKeys.ExceptWith_Count(prevKeys);
        int removed = prevKeys.ExceptWith_Count(newKeys);

        if (added == 0 && removed == 0)
        {
            // Same lineup — check guide
            var guideChanged = await GuideChangedAsync(prev.XmltvPath, newXmltvPath, ct);
            return guideChanged ? ChangeClasses.GuideOnly : ChangeClasses.None;
        }

        int total = Math.Max(prevKeys.Count, newKeys.Count);
        if (total == 0) return ChangeClasses.None;

        double churnRatio = (added + removed) / (double)total;
        return churnRatio > BreakingChurnThreshold
            ? ChangeClasses.Breaking
            : ChangeClasses.Lineup;
    }

    /// <summary>Merge two ChangeClass values, keeping the highest severity.</summary>
    public static string? Aggregate(string? a, string? b)
    {
        if (a is null && b is null) return null;
        return Severity(a) >= Severity(b) ? a : b;
    }

    private static int Severity(string? c) => c switch
    {
        ChangeClasses.Breaking  => 4,
        ChangeClasses.Lineup    => 3,
        ChangeClasses.GuideOnly => 2,
        ChangeClasses.None      => 1,
        _                       => 0, // null
    };

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task<KeySet> ReadKeysAsync(string idxPath, CancellationToken ct)
    {
        if (!File.Exists(idxPath))
            return new KeySet([]);

        await using var fs = new FileStream(
            idxPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65_536, useAsync: true);

        var header = new byte[HeaderSize];
        await fs.ReadExactlyAsync(header, ct);
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);

        if (count == 0)
            return new KeySet([]);

        var recordBytes = new byte[count * RecordSize];
        await fs.ReadExactlyAsync(recordBytes, ct);

        var keys = new HashSet<string>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var keySpan = recordBytes.AsSpan(i * RecordSize, KeySize);
            // Trim null padding and decode ASCII
            int len = keySpan.IndexOf((byte)0);
            if (len < 0) len = KeySize;
            keys.Add(Encoding.ASCII.GetString(keySpan[..len]));
        }

        return new KeySet(keys);
    }

    private static async Task<bool> GuideChangedAsync(string prevXmltvPath, string newXmltvPath, CancellationToken ct)
    {
        if (!File.Exists(prevXmltvPath) || !File.Exists(newXmltvPath))
            return false;

        var prevHash = await HashFileAsync(prevXmltvPath, ct);
        var newHash  = await HashFileAsync(newXmltvPath, ct);
        return !prevHash.AsSpan().SequenceEqual(newHash);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 131_072, useAsync: true);
        return await SHA256.HashDataAsync(fs, ct);
    }

    // Wrapper to expose set operations with a count without modifying the original set
    private sealed class KeySet(HashSet<string> inner)
    {
        public int Count => inner.Count;
        public bool Contains(string key) => inner.Contains(key);
        public IEnumerable<string> Keys => inner;

        public int ExceptWith_Count(KeySet other)
        {
            int count = 0;
            foreach (var key in inner)
            {
                if (!other.Contains(key))
                    count++;
            }
            return count;
        }
    }
}

public static class ChangeClasses
{
    public const string None      = "none";
    public const string GuideOnly = "guide_only";
    public const string Lineup    = "lineup";
    public const string Breaking  = "breaking";

    public static string ToUserLabel(string? changeClass) => changeClass switch
    {
        None      => "No changes",
        GuideOnly => "Guide updated",
        Lineup    => "Lineup updated",
        Breaking  => "Breaking change",
        _         => "Full refresh",
    };
}
