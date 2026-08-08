using System.Runtime.InteropServices;
using M3Undle.Web.Application;
using M3Undle.Web.Observability.Resources;

namespace M3Undle.Web.Observability;

/// <summary>
/// Writes a fixed three-line environment banner at startup.
/// </summary>
/// <remarks>
/// This exists for one scenario the Resources page cannot serve: the web server never comes up,
/// so the container log is the only channel. It runs before the first database access, because
/// that is where a bad /data mount stalls — a banner emitted afterwards would be missing exactly
/// when it is needed. Every field maps to a failure we have had to diagnose from a tester's log:
/// filesystem type and writability (mount and permission problems), free space (a full volume),
/// database and WAL size (growth and checkpoint starvation), and the runtime identifier
/// (architecture and emulation surprises). Keep it to three lines — this is triage, not telemetry.
/// </remarks>
public static class StartupEnvironmentLog
{
    public static void Write(ILogger logger, RuntimePaths runtimePaths)
    {
        var inContainer =
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            || File.Exists("/.dockerenv");

        logger.LogInformation(
            "Runtime: {RuntimeIdentifier}, {Framework}, container={InContainer}, user={User}, timezone={TimeZone}",
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.FrameworkDescription,
            inContainer,
            SafeUserName(),
            TimeZoneInfo.Local.Id);

        var configDirectory = Environment.GetEnvironmentVariable("M3UNDLE_CONFIG_DIR");

        // The data volume is described in full; the others are compared against it, because the
        // fact worth reading at a glance is whether logs and config sit on the same filesystem
        // or somewhere unexpected — repeating identical free-space figures three times buries it.
        var dataMount = MountPointOf(runtimePaths.DataDirectory);

        logger.LogInformation(
            "Storage: data={DataDirectory} [{DataVolume}]; logs={LogDirectory} [{LogVolume}]; config={ConfigDirectory} [{ConfigVolume}]",
            runtimePaths.DataDirectory,
            DescribeVolume(runtimePaths.DataDirectory, referenceMount: null),
            runtimePaths.LogDirectory,
            DescribeVolume(runtimePaths.LogDirectory, dataMount),
            configDirectory ?? "(not set)",
            configDirectory is null ? "n/a" : DescribeVolume(configDirectory, dataMount));

        logger.LogInformation("Database: {DatabasePath} [{DatabaseSize}]",
            runtimePaths.DatabasePath,
            DescribeDatabase(runtimePaths.DatabasePath));
    }

    /// <summary>
    /// Renders "ext4 at /data, writable, 41.2 GB free of 100.0 GB" for the filesystem holding
    /// <paramref name="path"/>, degrading to whatever parts are readable. When the path resolves
    /// to <paramref name="referenceMount"/> the figures are collapsed to "same volume as data".
    /// </summary>
    private static string DescribeVolume(string path, string? referenceMount)
    {
        if (!Directory.Exists(path))
            return "missing";

        var drive = MountResolver.ForPath(path);
        if (drive is null)
            return $"filesystem unknown, {Probe(path)}";

        string mountPoint;
        try
        {
            mountPoint = drive.RootDirectory.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"filesystem unknown, {Probe(path)}";
        }

        if (referenceMount is not null && string.Equals(mountPoint, referenceMount, StringComparison.Ordinal))
            return $"same volume as data, {Probe(path)}";

        try
        {
            return $"{drive.DriveFormat} at {mountPoint}, {Probe(path)}, "
                + $"{ResourceFactsPresentation.FormatBytes(drive.AvailableFreeSpace)} free of {ResourceFactsPresentation.FormatBytes(drive.TotalSize)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"at {mountPoint}, {Probe(path)}, free space unavailable";
        }
    }

    private static string? MountPointOf(string path)
    {
        try
        {
            return MountResolver.ForPath(path)?.RootDirectory.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Actually writes a file rather than inspecting permission bits: the failure we care about
    /// is a bind mount Docker created as root, where the mode looks fine but the write fails.
    /// </summary>
    private static string Probe(string path)
    {
        if (!Directory.Exists(path))
            return "missing";

        // Unique per call, not per process: two hosts can share a data directory (compose
        // scale-up, or the test suite booting factories in parallel), and a colliding probe
        // name would fail on FileShare.None and report a writable directory as read-only.
        var probePath = Path.Combine(path, $".m3undle-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(probePath, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
                stream.WriteByte(0);
            }

            return "writable";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"NOT WRITABLE ({ex.GetType().Name})";
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                    File.Delete(probePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort — DeleteOnClose already covers the normal path.
            }
        }
    }

    private static string DescribeDatabase(string databasePath)
    {
        try
        {
            var database = new FileInfo(databasePath);
            if (!database.Exists)
                return "not created yet";

            var wal = new FileInfo(databasePath + "-wal");
            var walNote = wal.Exists
                ? $" + {ResourceFactsPresentation.FormatBytes(wal.Length)} WAL"
                : string.Empty;

            return ResourceFactsPresentation.FormatBytes(database.Length) + walNote;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "size unavailable";
        }
    }

    private static string SafeUserName()
    {
        try
        {
            return Environment.UserName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return "unknown";
        }
    }
}
