namespace M3Undle.Web.Observability.Resources;

/// <summary>
/// Resolves the filesystem that actually contains a given path.
/// </summary>
/// <remarks>
/// Path.GetPathRoot returns "/" for every Unix path, so a DriveInfo built from it always
/// measures the root filesystem — inside a container that is the overlay, never the /data
/// volume. Matching the longest mount point that prefixes the path gives the real filesystem,
/// which matters whenever /data is a named volume, a bind mount to another disk, or a network
/// share: those are the cases where free space and locking behavior differ from the overlay.
/// </remarks>
public static class MountResolver
{
    /// <summary>
    /// Returns the drive whose mount point is the longest prefix of <paramref name="path"/>,
    /// or null if no mount matches or the drive table cannot be read.
    /// </summary>
    public static DriveInfo? ForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        DriveInfo? best = null;
        var bestLength = -1;

        foreach (var drive in drives)
        {
            string mountPoint;
            try
            {
                mountPoint = drive.RootDirectory.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!Contains(mountPoint, fullPath))
                continue;

            // Longest mount point wins: both "/" and "/data" prefix "/data/logs", but only
            // "/data" describes the filesystem the file actually lives on.
            if (mountPoint.Length > bestLength)
            {
                best = drive;
                bestLength = mountPoint.Length;
            }
        }

        return best;
    }

    private static bool Contains(string mountPoint, string fullPath)
    {
        if (string.IsNullOrEmpty(mountPoint))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(mountPoint, fullPath, comparison))
            return true;

        // Compare against the mount point with exactly one trailing separator so that "/data"
        // matches "/data/logs" but not "/database", and so the root mount still matches.
        var prefix = mountPoint.EndsWith(Path.DirectorySeparatorChar)
            ? mountPoint
            : mountPoint + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, comparison);
    }
}
