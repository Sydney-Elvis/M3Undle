using M3Undle.Web.Observability.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Observability.Resources;

[TestClass]
public sealed class MountResolverTests
{
    [TestMethod]
    public void ForPath_ResolvesTheDeepestMountContainingThePath()
    {
        // The bug this replaced: Path.GetPathRoot returns "/" for every Unix path, so a nested
        // path always resolved to the root filesystem. Whatever mount the resolver picks, the
        // path must actually live under it — and it must be the deepest such mount.
        var target = Directory.CreateTempSubdirectory("mount-resolver-tests-").FullName;
        try
        {
            var drive = MountResolver.ForPath(target);

            Assert.IsNotNull(drive, "A temp directory must resolve to some mounted filesystem.");

            var mountPoint = drive.RootDirectory.FullName;
            Assert.IsTrue(
                Path.GetFullPath(target).StartsWith(mountPoint.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFullPath(target) == mountPoint,
                $"Resolved mount '{mountPoint}' does not contain '{target}'.");

            var deeper = DriveInfo.GetDrives()
                .Where(d => Path.GetFullPath(target).StartsWith(
                    d.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                .Any(d => d.RootDirectory.FullName.Length > mountPoint.Length);

            Assert.IsFalse(deeper, $"A deeper mount than '{mountPoint}' also contains '{target}'.");
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [TestMethod]
    public void ForPath_DoesNotMatchOnSharedNamePrefix()
    {
        // "/data" must not be treated as the mount for "/database". Asserted through the public
        // surface: whatever each path resolves to must genuinely contain that path.
        foreach (var candidate in new[] { "/data", "/database" })
        {
            var drive = MountResolver.ForPath(candidate);
            if (drive is null)
                continue;

            var mountPoint = drive.RootDirectory.FullName.TrimEnd(Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(candidate);
            Assert.IsTrue(
                mountPoint.Length == 0 || full == mountPoint || full.StartsWith(mountPoint + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                $"'{candidate}' resolved to unrelated mount '{drive.RootDirectory.FullName}'.");
        }
    }

    [TestMethod]
    public void ForPath_ReturnsNullForBlankPath()
    {
        Assert.IsNull(MountResolver.ForPath(""));
        Assert.IsNull(MountResolver.ForPath("   "));
    }
}
