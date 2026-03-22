using M3Undle.Web.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Application;

/// <summary>
/// Checklist: Published /xmltv/m3undle.xml matches the active lineup.
/// Tests the XmlTvSerializer service layer — correct file is served when a valid snapshot
/// exists, and 503 is returned when there is no active snapshot or the file is missing.
///
/// Note: the deeper channel-content matching (every active channel appears in the XML and
/// nothing extra) requires the full integration harness (Python run_tests.py) because it
/// depends on the snapshot build pipeline writing the XMLTV file to disk.
/// </summary>
[TestClass]
public sealed class XmltvSerializerTests
{
    private readonly XmlTvSerializer _serializer = new();

    [TestMethod]
    public void Serialize_ValidXmltvPath_ReturnsPhysicalFile()
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "<tv></tv>");

        try
        {
            var lineup = MakeLineup(xmltvPath: tempPath);
            var result = _serializer.Serialize(lineup);

            Assert.IsInstanceOfType<PhysicalFileHttpResult>(result);
            var fileResult = (PhysicalFileHttpResult)result;
            Assert.AreEqual(tempPath, fileResult.FileName);
            Assert.AreEqual("application/xml; charset=utf-8", fileResult.ContentType);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [TestMethod]
    public void Serialize_NullXmltvPath_ReturnsProblemResult()
    {
        var lineup = MakeLineup(xmltvPath: null);
        var result = _serializer.Serialize(lineup);

        Assert.IsInstanceOfType<ProblemHttpResult>(result);
        var problem = (ProblemHttpResult)result;
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [TestMethod]
    public void Serialize_EmptyXmltvPath_ReturnsProblemResult()
    {
        var lineup = MakeLineup(xmltvPath: string.Empty);
        var result = _serializer.Serialize(lineup);

        Assert.IsInstanceOfType<ProblemHttpResult>(result);
        var problem = (ProblemHttpResult)result;
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [TestMethod]
    public void Serialize_XmltvPathDoesNotExistOnDisk_ReturnsProblemResult()
    {
        var lineup = MakeLineup(xmltvPath: "/nonexistent/path/m3undle.xml");
        var result = _serializer.Serialize(lineup);

        Assert.IsInstanceOfType<ProblemHttpResult>(result);
        var problem = (ProblemHttpResult)result;
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
    }

    [TestMethod]
    public void Serialize_ValidXmltvPath_ContentTypeIsApplicationXml()
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "<tv></tv>");

        try
        {
            var result = _serializer.Serialize(MakeLineup(xmltvPath: tempPath));
            var fileResult = (PhysicalFileHttpResult)result;
            StringAssert.StartsWith(fileResult.ContentType, "application/xml");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static RenderedLineup MakeLineup(string? xmltvPath) =>
        new(
            SnapshotId: "snap-1",
            ProfileId: "profile-1",
            SnapshotCreatedUtc: DateTime.UtcNow,
            ChannelIndexPath: "/fake/channels.idx",
            XmltvPath: xmltvPath,
            Channels: []);
}
