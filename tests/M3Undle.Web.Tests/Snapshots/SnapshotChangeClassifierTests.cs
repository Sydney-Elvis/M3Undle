using M3Undle.Web.Application;
using M3Undle.Web.Data.Entities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Snapshots;

[TestClass]
public sealed class SnapshotChangeClassifierTests
{
    [TestMethod]
    public async Task ClassifyAsync_WhenNoPreviousSnapshot_ReturnsNull()
    {
        var tempDir = CreateTempDir();
        try
        {
            var (newIndexPath, newXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "next",
                [Entry("a1"), Entry("a2")],
                "<tv><channel id='a'/></tv>");

            var result = await SnapshotChangeClassifier.ClassifyAsync(
                prev: null,
                newIndexPath,
                newXmltvPath,
                CancellationToken.None);

            Assert.IsNull(result);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    [TestMethod]
    public async Task ClassifyAsync_WhenLineupAndGuideUnchanged_ReturnsNone()
    {
        var tempDir = CreateTempDir();
        try
        {
            var (prevIndexPath, prevXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "prev",
                [Entry("a1"), Entry("a2"), Entry("a3")],
                "<tv><channel id='a'/></tv>");

            var (newIndexPath, newXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "next",
                [Entry("a1"), Entry("a2"), Entry("a3")],
                "<tv><channel id='a'/></tv>");

            var prev = new Snapshot
            {
                SnapshotId = "prev",
                ProfileId = "profile-1",
                ChannelIndexPath = prevIndexPath,
                XmltvPath = prevXmltvPath,
            };

            var result = await SnapshotChangeClassifier.ClassifyAsync(
                prev,
                newIndexPath,
                newXmltvPath,
                CancellationToken.None);

            Assert.AreEqual(ChangeClasses.None, result);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    [TestMethod]
    public async Task ClassifyAsync_WhenLineupSameButGuideChanged_ReturnsGuideOnly()
    {
        var tempDir = CreateTempDir();
        try
        {
            var (prevIndexPath, prevXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "prev",
                [Entry("a1"), Entry("a2"), Entry("a3")],
                "<tv><programme start='20260101000000 +0000'/></tv>");

            var (newIndexPath, newXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "next",
                [Entry("a1"), Entry("a2"), Entry("a3")],
                "<tv><programme start='20260102000000 +0000'/></tv>");

            var prev = new Snapshot
            {
                SnapshotId = "prev",
                ProfileId = "profile-1",
                ChannelIndexPath = prevIndexPath,
                XmltvPath = prevXmltvPath,
            };

            var result = await SnapshotChangeClassifier.ClassifyAsync(
                prev,
                newIndexPath,
                newXmltvPath,
                CancellationToken.None);

            Assert.AreEqual(ChangeClasses.GuideOnly, result);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    [TestMethod]
    public async Task ClassifyAsync_WhenChurnWithinThreshold_ReturnsLineup()
    {
        var tempDir = CreateTempDir();
        try
        {
            var (prevIndexPath, prevXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "prev",
                [Entry("a1"), Entry("a2"), Entry("a3"), Entry("a4"), Entry("a5")],
                "<tv/>");

            var (newIndexPath, newXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "next",
                [Entry("a1"), Entry("a2"), Entry("a3"), Entry("a4"), Entry("a5"), Entry("a6")],
                "<tv/>");

            var prev = new Snapshot
            {
                SnapshotId = "prev",
                ProfileId = "profile-1",
                ChannelIndexPath = prevIndexPath,
                XmltvPath = prevXmltvPath,
            };

            var result = await SnapshotChangeClassifier.ClassifyAsync(
                prev,
                newIndexPath,
                newXmltvPath,
                CancellationToken.None);

            Assert.AreEqual(ChangeClasses.Lineup, result);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    [TestMethod]
    public async Task ClassifyAsync_WhenChurnExceedsThreshold_ReturnsBreaking()
    {
        var tempDir = CreateTempDir();
        try
        {
            var (prevIndexPath, prevXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "prev",
                [Entry("a1"), Entry("a2"), Entry("a3"), Entry("a4"), Entry("a5")],
                "<tv/>");

            var (newIndexPath, newXmltvPath) = await WriteSnapshotFilesAsync(
                tempDir,
                "next",
                [Entry("a1")],
                "<tv/>");

            var prev = new Snapshot
            {
                SnapshotId = "prev",
                ProfileId = "profile-1",
                ChannelIndexPath = prevIndexPath,
                XmltvPath = prevXmltvPath,
            };

            var result = await SnapshotChangeClassifier.ClassifyAsync(
                prev,
                newIndexPath,
                newXmltvPath,
                CancellationToken.None);

            Assert.AreEqual(ChangeClasses.Breaking, result);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    private static ChannelIndexEntry Entry(string streamKey)
        => new(
            StreamKey: streamKey,
            DisplayName: $"Channel {streamKey}",
            TvgId: null,
            TvgName: null,
            LogoUrl: null,
            GroupTitle: "Group",
            TvgChno: null,
            ProviderChannelId: $"provider-{streamKey}",
            StreamUrl: $"https://example.test/{streamKey}.ts");

    private static async Task<(string IndexPath, string XmltvPath)> WriteSnapshotFilesAsync(
        string root,
        string name,
        IReadOnlyList<ChannelIndexEntry> entries,
        string xmltvContent)
    {
        var snapshotDir = Path.Combine(root, name);
        Directory.CreateDirectory(snapshotDir);

        var ndjsonPath = Path.Combine(snapshotDir, "channel_index.ndjson");
        var idxPath = Path.Combine(snapshotDir, "channel_index.idx");
        await ChannelIndexStore.WriteAsync(ndjsonPath, idxPath, entries, CancellationToken.None);

        var xmltvPath = Path.Combine(snapshotDir, "guide.xml");
        await File.WriteAllTextAsync(xmltvPath, xmltvContent, CancellationToken.None);

        return (ndjsonPath, xmltvPath);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"m3undle-snapshot-classifier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort cleanup in tests
        }
    }
}
