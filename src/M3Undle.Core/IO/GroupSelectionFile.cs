namespace M3Undle.Core.IO;

public static class GroupSelectionFile
{
    public static HashSet<string> LoadKeepSet(string path)
    {
        return LoadSelection(path).Keep;
    }

    public static GroupSelection LoadSelection(string path)
    {
        try
        {
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                if (line.StartsWith("######", StringComparison.Ordinal))
                {
                    continue;
                }

                var isPending = line.StartsWith("##", StringComparison.Ordinal);
                var normalized = line.TrimStart('#').Trim();
                if (string.IsNullOrEmpty(normalized))
                {
                    continue;
                }

                all.Add(normalized);

                if (isPending)
                {
                    pending.Add(normalized);
                }

                if (line.StartsWith('#'))
                {
                    keep.Add(normalized);
                }
            }

            return new GroupSelection(keep, all, pending);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"Failed to read groups file '{path}': {ex.Message}", ExitCodes.IoError);
        }
    }

    public sealed record GroupSelection(HashSet<string> Keep, HashSet<string> All, HashSet<string> PendingReview);
}

