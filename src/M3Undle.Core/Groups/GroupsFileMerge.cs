namespace M3Undle.Core.Groups;

public static class GroupsFileMerge
{
    private const string VersionPrefix = "######  Created with bndl version ";
    private const int VersionLineLength = 88;
    private const string VersionLineTrailer = " ######";

    public static GroupsFileMergeResult Merge(
        IReadOnlyList<string> existingLines,
        IEnumerable<string> discoveredGroups,
        string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(existingLines);
        ArgumentNullException.ThrowIfNull(discoveredGroups);

        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            throw new ArgumentException("Current version is required.", nameof(currentVersion));
        }

        var existingGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputLines = new List<string>();
        var headerProcessed = false;
        var hasVersionLine = false;

        foreach (var line in existingLines)
        {
            if (line.TrimStart().StartsWith(VersionPrefix, StringComparison.Ordinal))
            {
                outputLines.Add(CreateVersionLine(currentVersion));
                hasVersionLine = true;
                headerProcessed = true;
                continue;
            }

            outputLines.Add(line);

            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("######", StringComparison.Ordinal))
            {
                headerProcessed = true;
                continue;
            }

            if (headerProcessed)
            {
                var trimmed = line.TrimStart();
                var groupName = trimmed.TrimStart('#').Trim();
                if (!string.IsNullOrWhiteSpace(groupName))
                {
                    existingGroups.Add(groupName);
                }
            }
        }

        if (!hasVersionLine)
        {
            outputLines.Insert(FindVersionInsertIndex(outputLines), CreateVersionLine(currentVersion));
        }

        var newGroups = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in discoveredGroups)
        {
            if (!existingGroups.Contains(group))
            {
                newGroups.Add(group);
            }
        }

        foreach (var newGroup in newGroups)
        {
            outputLines.Add($"##{newGroup}");
        }

        return new GroupsFileMergeResult(outputLines, newGroups);
    }

    private static int FindVersionInsertIndex(IReadOnlyList<string> outputLines)
    {
        var insertIndex = 0;
        for (var i = 0; i < outputLines.Count; i++)
        {
            if (outputLines[i].TrimStart().StartsWith("######", StringComparison.Ordinal))
            {
                insertIndex = i + 1;
            }
            else if (!string.IsNullOrWhiteSpace(outputLines[i]))
            {
                break;
            }
        }

        return insertIndex;
    }

    private static string CreateVersionLine(string currentVersion)
    {
        var versionLine = $"{VersionPrefix}{currentVersion}";
        return versionLine.PadRight(VersionLineLength - VersionLineTrailer.Length) + VersionLineTrailer;
    }
}

public sealed record GroupsFileMergeResult(
    IReadOnlyList<string> OutputLines,
    IReadOnlySet<string> NewGroups);
