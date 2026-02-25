using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using M3Undle.Core.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.IO;

[TestClass]
public class TextFileWriterTests
{
    [TestMethod]
    public async Task WriteAtomicAsync_WritesLinesToFile()
    {
        var tmp = Path.GetTempFileName();
        var lines = new List<string> { "line1", "line2" };
        await TextFileWriter.WriteAtomicAsync(tmp, lines, CancellationToken.None);
        var read = await File.ReadAllLinesAsync(tmp);
        CollectionAssert.AreEqual(lines, read);
        File.Delete(tmp);
    }

    [TestMethod]
    public async Task WriteAtomicTextAsync_WritesTextToFile()
    {
        var tmp = Path.GetTempFileName();
        var content = "hello world";
        await TextFileWriter.WriteAtomicTextAsync(tmp, content, CancellationToken.None);
        var read = await File.ReadAllTextAsync(tmp);
        Assert.AreEqual(content, read);
        File.Delete(tmp);
    }

    [TestMethod]
    public async Task WriteAtomicAsync_CreatesDirectoryIfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var file = Path.Combine(dir, "test.txt");
        var lines = new List<string> { "abc" };
        await TextFileWriter.WriteAtomicAsync(file, lines, CancellationToken.None);
        Assert.IsTrue(File.Exists(file));
        File.Delete(file);
        Directory.Delete(dir);
    }

    [TestMethod]
    public async Task WriteAtomicTextAsync_CreatesDirectoryIfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var file = Path.Combine(dir, "test.txt");
        var content = "abc";
        await TextFileWriter.WriteAtomicTextAsync(file, content, CancellationToken.None);
        Assert.IsTrue(File.Exists(file));
        File.Delete(file);
        Directory.Delete(dir);
    }
}

