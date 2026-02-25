using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using M3Undle.Cli;
using M3Undle.Cli.Commands;
using M3Undle.Core.IO;
using M3Undle.Core.M3u;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.Commands;

[TestClass]
[DoNotParallelize]
public class GroupsCommandTests
{
    [TestMethod]
    public async Task ExecuteAsync_CreatesNewFile_WhenFileDoesNotExist()
    {
        var tmpFile = Path.GetTempFileName();
        File.Delete(tmpFile); // Delete so we can test creation
        
        try
        {
            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""News"",Channel 2
http://example.com/2";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups, 
                new CommandOptionSet(new Dictionary<string, List<string>>()), 
                null, null, null, 
                new Dictionary<string, string>(), 
                "http://test", 
                null, null, 
                tmpFile, 
                null, null, 
                false, false);
            
            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            Assert.AreEqual(0, result);
            Assert.IsTrue(File.Exists(tmpFile));
            
            var lines = await File.ReadAllLinesAsync(tmpFile);
            Assert.IsGreaterThan(3, lines.Length); // Header + groups
            CollectionAssert.Contains(lines, "News");
            CollectionAssert.Contains(lines, "Sports");
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_AddsOnlyNewGroups_WhenFileExists()
    {
        var tmpFile = Path.GetTempFileName();
        
        try
        {
            // Create existing file with some groups - use current major version so the major check passes
            var currentMajor = GroupsFileValidator.GetCurrentVersion().Split('.')[0];
            var existingContent = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with bndl version {currentMajor}.0 ######

#Sports
News
Entertainment";
            await File.WriteAllTextAsync(tmpFile, existingContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""News"",Channel 2
http://example.com/2
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 3"" group-title=""Movies"",Channel 3
http://example.com/3";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups, 
                new CommandOptionSet(new Dictionary<string, List<string>>()), 
                null, null, null, 
                new Dictionary<string, string>(), 
                "http://test", 
                null, null, 
                tmpFile, 
                null, null, 
                false, false);
            
            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            Assert.AreEqual(0, result);
            
            var lines = await File.ReadAllLinesAsync(tmpFile);
            
            // Existing groups should still be there
            CollectionAssert.Contains(lines, "#Sports");
            CollectionAssert.Contains(lines, "News");
            CollectionAssert.Contains(lines, "Entertainment");
            
            // New group should be added with ## prefix
            CollectionAssert.Contains(lines, "##Movies");
            
            // Should not duplicate existing groups
            var sportsCount = lines.Count(line => 
            {
                var trimmed = line.TrimStart().TrimStart('#').Trim();
                return trimmed.Equals("Sports", StringComparison.OrdinalIgnoreCase);
            });
            Assert.AreEqual(1, sportsCount);
            
            // Backup should exist
            var backupPath = $"{tmpFile}.bak";
            Assert.IsTrue(File.Exists(backupPath));
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_HandlesEmptyLinesInExistingFile()
    {
        var tmpFile = Path.GetTempFileName();
        
        try
        {
            var currentMajor = GroupsFileValidator.GetCurrentVersion().Split('.')[0];
            var existingContent = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with bndl version {currentMajor}.0 ######

#Sports

News

";
            await File.WriteAllTextAsync(tmpFile, existingContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""Movies"",Channel 2
http://example.com/2";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups, 
                new CommandOptionSet(new Dictionary<string, List<string>>()), 
                null, null, null, 
                new Dictionary<string, string>(), 
                "http://test", 
                null, null, 
                tmpFile, 
                null, null, 
                false, false);
            
            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            Assert.AreEqual(0, result);
            
            var lines = await File.ReadAllLinesAsync(tmpFile);
            
            // Should preserve blank lines from original
            Assert.IsTrue(lines.Any(line => string.IsNullOrWhiteSpace(line)));
            
            // Should add new group with ## prefix
            CollectionAssert.Contains(lines, "##Movies");
            
            // Cleanup backup
            var backupPath = $"{tmpFile}.bak";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_NoNewGroups_WhenAllExist()
    {
        var tmpFile = Path.GetTempFileName();
        
        try
        {
            // Use current version to ensure version matches and no backup is created
            var currentVersion = GroupsFileValidator.GetCurrentVersion();
            var existingContent = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with bndl version {currentVersion}                                          ######

#Sports
News";
            await File.WriteAllTextAsync(tmpFile, existingContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""News"",Channel 2
http://example.com/2";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups, 
                new CommandOptionSet(new Dictionary<string, List<string>>()), 
                null, null, null, 
                new Dictionary<string, string>(), 
                "http://test", 
                null, null, 
                tmpFile, 
                null, null, 
                false, false);
            
            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            Assert.AreEqual(0, result);
            
            // Backup should NOT exist since nothing changed (no new groups, version matches)
            var backupPath = $"{tmpFile}.bak";
            Assert.IsFalse(File.Exists(backupPath));
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_CaseInsensitiveGroupMatching()
    {
        var tmpFile = Path.GetTempFileName();
        
        try
        {
            var currentMajor = GroupsFileValidator.GetCurrentVersion().Split('.')[0];
            var existingContent = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with bndl version {currentMajor}.0 ######

#SPORTS";
            await File.WriteAllTextAsync(tmpFile, existingContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""Sports"",Channel 2
http://example.com/2";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups, 
                new CommandOptionSet(new Dictionary<string, List<string>>()), 
                null, null, null, 
                new Dictionary<string, string>(), 
                "http://test", 
                null, null, 
                tmpFile, 
                null, null, 
                false, false);
            
            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            Assert.AreEqual(0, result);
            
            var lines = await File.ReadAllLinesAsync(tmpFile);
            
            // Should not duplicate "sports" in different cases
            var sportsVariants = lines.Count(line => 
            {
                var trimmed = line.TrimStart().TrimStart('#').Trim();
                return trimmed.Equals("sports", StringComparison.OrdinalIgnoreCase);
            });
            Assert.AreEqual(1, sportsVariants);
            
            // Cleanup backup since no new groups were added
            var backupPath = $"{tmpFile}.bak";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsInvalidFile()
    {
        var tmpFile = Path.GetTempFileName();
        
        try
        {
            // Create a file that doesn't have the proper header
            var invalidContent = @"This is not a groups file
Just some random content
Sports
News";
            await File.WriteAllTextAsync(tmpFile, invalidContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups, 
                new CommandOptionSet(new Dictionary<string, List<string>>()), 
                null, null, null, 
                new Dictionary<string, string>(), 
                "http://test", 
                null, null, 
                tmpFile, 
                null, null, 
                false, false);
            
            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            // Should return error code
            Assert.AreNotEqual(0, result);
            
            // File should NOT have been modified (still has invalid content)
            var fileContent = await File.ReadAllTextAsync(tmpFile);
            StringAssert.Contains(fileContent, "This is not a groups file");
            
            // No backup should have been created since we didn't modify
            var backupPath = $"{tmpFile}.bak";
            Assert.IsFalse(File.Exists(backupPath));
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_RejectsMajorVersionMismatch()
    {
        var tmpFile = Path.GetTempFileName();
        
        try
        {
            // Create a file with a different major version
            var oldVersionContent = @"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with bndl version 99.0 ######

#Sports";
            await File.WriteAllTextAsync(tmpFile, oldVersionContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups, 
                new CommandOptionSet(new Dictionary<string, List<string>>()), 
                null, null, null, 
                new Dictionary<string, string>(), 
                "http://test", 
                null, null, 
                tmpFile, 
                null, null, 
                false, false);
            
            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            // Should return error code
            Assert.AreNotEqual(0, result);
            
            // File should NOT have been modified (still has version 99.0)
            var fileContent = await File.ReadAllTextAsync(tmpFile);
            StringAssert.Contains(fileContent, "version 99.0");
            
            // No backup should have been created since we didn't modify
            var backupPath = $"{tmpFile}.bak";
            Assert.IsFalse(File.Exists(backupPath));
        }
        finally
        {
            if (File.Exists(tmpFile))
            {
                File.Delete(tmpFile);
            }
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ForceFlag_AllowsInvalidFile()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // Create a file that doesn't have the proper header
            var invalidContent = @"This is not a groups file
Just some random content
Sports
News";
            await File.WriteAllTextAsync(tmpFile, invalidContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""Movies"",Channel 2
http://example.com/2";

            var options = new Dictionary<string, List<string>> { { "force", new List<string> { CommandOptionParser.FlagPresentValue } } };
            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups,
                new CommandOptionSet(options),
                null, null, null,
                new Dictionary<string, string>(),
                "http://test",
                null, null,
                tmpFile,
                null, null,
                false, false);

            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            // Should succeed with --force
            Assert.AreEqual(0, result);
            
            // File should be modified with new groups
            var lines = await File.ReadAllLinesAsync(tmpFile);
            CollectionAssert.Contains(lines, "##Movies");
            
            // Backup should have been created
            var backupPath = $"{tmpFile}.bak";
            Assert.IsTrue(File.Exists(backupPath));
            
            // Cleanup
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_ForceFlag_AllowsVersionMismatch()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // Create a file with a different major version
            var oldVersionContent = @"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with bndl version 99.0 ######

#Sports";
            await File.WriteAllTextAsync(tmpFile, oldVersionContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""Movies"",Channel 2
http://example.com/2";

            var options = new Dictionary<string, List<string>> { { "force", new List<string> { CommandOptionParser.FlagPresentValue } } };
            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups,
                new CommandOptionSet(options),
                null, null, null,
                new Dictionary<string, string>(),
                "http://test",
                null, null,
                tmpFile,
                null, null,
                false, false);

            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            // Should succeed with --force
            Assert.AreEqual(0, result);
            
            // File should be modified with new groups
            var lines = await File.ReadAllLinesAsync(tmpFile);
            CollectionAssert.Contains(lines, "##Movies");
            
            // Backup should have been created
            var backupPath = $"{tmpFile}.bak";
            Assert.IsTrue(File.Exists(backupPath));
            
            // Cleanup
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_UpdatesVersion_WhenMinorVersionDiffers()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // Create a file with the same major version but different minor version
            // This simulates an upgrade scenario where the file format is compatible
            var currentVersion = GroupsFileValidator.GetCurrentVersion();
            var majorVersion = currentVersion.Split('.')[0];
            var differentMinorVersion = $"{majorVersion}.5"; // Different minor version
            
            var oldVersionContent = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with bndl version {differentMinorVersion}                                          ######

#Sports
News";
            await File.WriteAllTextAsync(tmpFile, oldVersionContent);

            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""News"",Channel 2
http://example.com/2
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 3"" group-title=""Movies"",Channel 3
http://example.com/3";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups,
                new CommandOptionSet(new Dictionary<string, List<string>>()),
                null, null, null,
                new Dictionary<string, string>(),
                "http://test",
                null, null,
                tmpFile,
                null, null,
                false, false);

            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            // Should succeed
            Assert.AreEqual(0, result);
            
            // Read the updated file
            var lines = await File.ReadAllLinesAsync(tmpFile);
            
            // Should contain the new group
            CollectionAssert.Contains(lines, "##Movies");
            
            // Version line should be updated to current version
            var versionLine = lines.FirstOrDefault(l => l.Contains("Created with bndl version"));
            Assert.IsNotNull(versionLine);
            StringAssert.Contains(versionLine, $"version {currentVersion}");
            
            // Backup should have been created since we made changes
            var backupPath = $"{tmpFile}.bak";
            Assert.IsTrue(File.Exists(backupPath));
            
            // Verify backup has the old version
            var backupLines = await File.ReadAllLinesAsync(backupPath);
            var backupVersionLine = backupLines.FirstOrDefault(l => l.Contains("Created with bndl version"));
            Assert.IsNotNull(backupVersionLine);
            StringAssert.Contains(backupVersionLine, $"version {differentMinorVersion}");
            
            // Cleanup
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_UpdatesVersionOnly_WhenNoNewGroups()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // Create a file with the same major version but different minor version
            var currentVersion = GroupsFileValidator.GetCurrentVersion();
            var majorVersion = currentVersion.Split('.')[0];
            var differentMinorVersion = $"{majorVersion}.5.0";
            
            var oldVersionContent = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with bndl version {differentMinorVersion}                                          ######

#Sports
News";
            await File.WriteAllTextAsync(tmpFile, oldVersionContent);

            // Playlist has only existing groups, no new ones
            var playlistContent = @"#EXTM3U
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 1"" group-title=""Sports"",Channel 1
http://example.com/1
#EXTINF:-1 tvg-id="""" tvg-name=""Channel 2"" group-title=""News"",Channel 2
http://example.com/2";

            var cmd = CreateGroupsCommand(playlistContent);
            var context = new CommandContext(
                CommandKind.Groups,
                new CommandOptionSet(new Dictionary<string, List<string>>()),
                null, null, null,
                new Dictionary<string, string>(),
                "http://test",
                null, null,
                tmpFile,
                null, null,
                false, false);

            var result = await cmd.ExecuteAsync(context, CancellationToken.None);
            
            // Should succeed
            Assert.AreEqual(0, result);
            
            // Read the updated file
            var lines = await File.ReadAllLinesAsync(tmpFile);
            
            // Version line should be updated to current version
            var versionLine = lines.FirstOrDefault(l => l.Contains("Created with bndl version"));
            Assert.IsNotNull(versionLine);
            StringAssert.Contains(versionLine, $"version {currentVersion}");
            
            // Backup should have been created even though no new groups were added (version was updated)
            var backupPath = $"{tmpFile}.bak";
            Assert.IsTrue(File.Exists(backupPath));
            
            // Cleanup
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (File.Exists(tmpFile)) File.Delete(tmpFile);
        }
    }

    [TestMethod]
    public void CreateHeader_AllLinesAreSameLength()
    {
        var header = GroupsFileValidator.CreateHeader();
        
        // All header lines (excluding the empty line at the end) should be exactly 88 characters
        const int expectedLength = 88;
        
        for (int i = 0; i < header.Length - 1; i++) // Skip the last empty line
        {
            var line = header[i];
            Assert.AreEqual(expectedLength, line.Length, 
                $"Header line {i} has length {line.Length} but expected {expectedLength}: '{line}'");
        }
        
        // Last line should be empty
        Assert.AreEqual(string.Empty, header[^1]);
    }

    [TestMethod]
    public void GetCurrentVersion_IncludesBuildNumber()
    {
        var version = GroupsFileValidator.GetCurrentVersion();
        
        // Should have format X.Y.Z (e.g., 0.41.1)
        var parts = version.Split('.');
        Assert.HasCount(3, parts, $"Version '{version}' should have 3 parts (Major.Minor.Build)");
        
        // All parts should be numeric
        foreach (var part in parts)
        {
            Assert.IsTrue(int.TryParse(part, out _), $"Version part '{part}' should be numeric");
        }
    }

    private static GroupsCommand CreateGroupsCommand(string playlistResponse)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var diagnostics = new StringWriter();
        var httpClient = new HttpClient(new MockHttpMessageHandler(playlistResponse));
        var parser = new PlaylistParser();
        
        return new GroupsCommand(stdout, stderr, diagnostics, httpClient, parser);
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _response;
        public MockHttpMessageHandler(string response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(_response) });
    }
}

