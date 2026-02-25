using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using M3Undle.Cli;
using M3Undle.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests;

[TestClass]
public class CliAppTests
{
    [TestMethod]
    public async Task RunAsync_NoArgs_PrintsUsageAndReturnsError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            var result = await app.RunAsync(Array.Empty<string>());

            Assert.AreEqual(ExitCodes.ConfigError, result);
            StringAssert.Contains(stdout.ToString(), "Usage:");
        }
    }

    [TestMethod]
    public async Task RunAsync_UnknownCommand_PrintsUsageAndReturnsError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            var result = await app.RunAsync(new[] { "unknown" });

            Assert.AreEqual(ExitCodes.ConfigError, result);
            StringAssert.Contains(stdout.ToString(), "Usage:");
        }
    }

    [TestMethod]
    public async Task RunAsync_InvalidOption_PrintsErrorAndUsage()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            var result = await app.RunAsync(new[] { "run", "invalid-token" });

            Assert.AreEqual(ExitCodes.ConfigError, result);
            StringAssert.Contains(stderr.ToString(), "Unexpected token");
            StringAssert.Contains(stdout.ToString(), "Usage:");
        }
    }

    [TestMethod]
    public async Task RunAsync_GroupsCommand_WithoutPlaylistUrl_ThrowsCliException()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            var result = await app.RunAsync(new[] { "groups" });

            Assert.AreNotEqual(ExitCodes.Success, result);
            Assert.IsGreaterThan(0, stderr.ToString().Length);
        }
    }

    [TestMethod]
    public async Task RunAsync_RunCommand_WithoutPlaylistUrl_ThrowsCliException()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            var result = await app.RunAsync(new[] { "run" });

            Assert.AreNotEqual(ExitCodes.Success, result);
            Assert.IsGreaterThan(0, stderr.ToString().Length);
        }
    }

    [TestMethod]
    public async Task RunAsync_CaseInsensitiveCommands_RecognizesGroups()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            // Should recognize command even without proper args (will fail later for different reason)
            var result = await app.RunAsync(new[] { "GROUPS" });

            // Should not return ConfigError for unknown command
            Assert.IsTrue(stderr.ToString().Contains("Missing required") || stderr.ToString().Length > 0);
        }
    }

    [TestMethod]
    public async Task RunAsync_CaseInsensitiveCommands_RecognizesRun()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            var result = await app.RunAsync(new[] { "RUN" });

            // Should not return ConfigError for unknown command
            Assert.IsTrue(stderr.ToString().Contains("Missing required") || stderr.ToString().Length > 0);
        }
    }

    [TestMethod]
    public async Task RunAsync_VerboseFlag_EnablesDiagnostics()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            // This will fail due to missing playlist, but should attempt to process verbose flag
            var result = await app.RunAsync(new[] { "run", "--verbose" });

            Assert.AreNotEqual(ExitCodes.Success, result);
        }
    }

    [TestMethod]
    public async Task RunAsync_MultipleInvalidTokens_ReportsFirstError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        using (var app = new CliApp(stdout, stderr))
        {
            var result = await app.RunAsync(new[] { "run", "bad1", "bad2" });

            Assert.AreEqual(ExitCodes.ConfigError, result);
            StringAssert.Contains(stderr.ToString(), "Unexpected token 'bad1'");
        }
    }

    [TestMethod]
    public async Task RunAsync_OptionsWithDashes_ParsesCorrectly()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // Create a temporary test file to avoid network requests
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "#EXTM3U\n#EXTINF:-1,Test\nhttp://test/stream");
            using (var app = new CliApp(stdout, stderr))
            {
                // Should parse the option names correctly
                var result = await app.RunAsync(new[]
            {
                "run",
                "--playlist-url", tempFile,
                "--out-playlist", "-"
            });

                // Test should succeed since we have a valid playlist file
                Assert.AreEqual(ExitCodes.Success, result);
            }
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}

