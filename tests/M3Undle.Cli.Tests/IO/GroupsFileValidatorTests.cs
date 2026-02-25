using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using M3Undle.Cli;
using M3Undle.Core.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Cli.Tests.IO;

[TestClass]
public class GroupsFileValidatorTests
{
    [TestMethod]
    public async Task ValidateFileAsync_NonExistentFile_ReturnsValid()
    {
        var result = await GroupsFileValidator.ValidateFileAsync("nonexistent.txt", CancellationToken.None);
        
        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.FileVersion);
        Assert.IsNull(result.ErrorMessage);
    }

    [TestMethod]
    public async Task ValidateFileAsync_ValidFile_ReturnsValid()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var currentVersion = GroupsFileValidator.GetCurrentVersion();
            var content = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with iptv version {currentVersion} ######

#Sports
News";
            await File.WriteAllTextAsync(tmpFile, content);

            var result = await GroupsFileValidator.ValidateFileAsync(tmpFile, CancellationToken.None);

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(currentVersion, result.FileVersion);
            Assert.IsNull(result.ErrorMessage);
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
    public async Task ValidateFileAsync_InvalidFile_ReturnsInvalid()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var content = @"This is not a groups file
Just some random content";
            await File.WriteAllTextAsync(tmpFile, content);

            var result = await GroupsFileValidator.ValidateFileAsync(tmpFile, CancellationToken.None);

            Assert.IsFalse(result.IsValid);
            Assert.IsNotNull(result.ErrorMessage);
            StringAssert.Contains(result.ErrorMessage, "missing header");
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
    public async Task ValidateFileAsync_MajorVersionMismatch_ReturnsInvalid()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var content = @"######  This is a DROP list. Put a '#' in front of any group you want to KEEP. ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.              ######
######  Created with iptv version 99.0 ######

#Sports";
            await File.WriteAllTextAsync(tmpFile, content);

            var result = await GroupsFileValidator.ValidateFileAsync(tmpFile, CancellationToken.None);

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual("99.0", result.FileVersion);
            Assert.IsNotNull(result.ErrorMessage);
            StringAssert.Contains(result.ErrorMessage, "major version mismatch");
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
    public async Task ValidateFileAsync_MinorVersionDifferent_ReturnsValid()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var currentVersion = GroupsFileValidator.GetCurrentVersion();
            var parts = currentVersion.Split('.');
            var differentMinor = int.Parse(parts[1]) + 1;
            var testVersion = $"{parts[0]}.{differentMinor}";

            var content = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with iptv version {testVersion} ######

#Sports";
            await File.WriteAllTextAsync(tmpFile, content);

            var result = await GroupsFileValidator.ValidateFileAsync(tmpFile, CancellationToken.None);

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(testVersion, result.FileVersion);
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
    public async Task ValidateFileAsync_NoVersion_ReturnsInvalid()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var content = @"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######

#Sports
News";
            await File.WriteAllTextAsync(tmpFile, content);

            var result = await GroupsFileValidator.ValidateFileAsync(tmpFile, CancellationToken.None);

            Assert.IsFalse(result.IsValid);
            Assert.IsNull(result.FileVersion);
            Assert.IsNotNull(result.ErrorMessage);
            StringAssert.Contains(result.ErrorMessage, "does not contain version information");
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
    public async Task ValidateFileAsync_OldHeaderFormat_ReturnsValid()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // Old format without the extra padding — use current major version so the major check passes
            var currentMajor = GroupsFileValidator.GetCurrentVersion().Split('.')[0];
            var testVersion = $"{currentMajor}.0";
            var content = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP. ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.              ######
######  Created with iptv version {testVersion} ######

#Sports
News";
            await File.WriteAllTextAsync(tmpFile, content);

            var result = await GroupsFileValidator.ValidateFileAsync(tmpFile, CancellationToken.None);

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(testVersion, result.FileVersion);
            Assert.IsNull(result.ErrorMessage);
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
    public async Task ValidateFileAsync_NewHeaderFormat_ReturnsValid()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            // New format with proper padding and alignment — use current major version so the major check passes
            var currentMajor = GroupsFileValidator.GetCurrentVersion().Split('.')[0];
            var testVersion = $"{currentMajor}.0";
            var content = $@"######  This is a DROP list. Put a '#' in front of any group you want to KEEP.  ######
######  Lines without '#' will be DROPPED. Blank lines are ignored.             ######
######  Created with iptv version {testVersion}                                          ######

#Sports
News";
            await File.WriteAllTextAsync(tmpFile, content);

            var result = await GroupsFileValidator.ValidateFileAsync(tmpFile, CancellationToken.None);

            Assert.IsTrue(result.IsValid);
            Assert.AreEqual(testVersion, result.FileVersion);
            Assert.IsNull(result.ErrorMessage);
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
    public void CreateBackupPath_NoExistingBackup_ReturnsBasicBackup()
    {
        var originalPath = Path.Combine(Path.GetTempPath(), "test.txt");
        var expectedBackup = Path.Combine(Path.GetTempPath(), "test.txt.bak");

        var backupPath = GroupsFileValidator.CreateBackupPath(originalPath);

        Assert.AreEqual(expectedBackup, backupPath);
    }

    [TestMethod]
    public void CreateBackupPath_ExistingBackup_ReturnsNumberedBackup()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);

        try
        {
            var originalPath = Path.Combine(tmpDir, "test.txt");
            var bakPath = Path.Combine(tmpDir, "test.txt.bak");
            
            // Create existing backup
            File.WriteAllText(bakPath, "existing backup");

            var backupPath = GroupsFileValidator.CreateBackupPath(originalPath);
            var expectedBackup = Path.Combine(tmpDir, "test.txt.bak1");

            Assert.AreEqual(expectedBackup, backupPath);
        }
        finally
        {
            if (Directory.Exists(tmpDir))
            {
                Directory.Delete(tmpDir, true);
            }
        }
    }

    [TestMethod]
    public async Task CreateBackupAsync_CreatesBackupFile()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var content = "Test content for backup";
            await File.WriteAllTextAsync(tmpFile, content);

            var backupPath = GroupsFileValidator.CreateBackupPath(tmpFile);
            await GroupsFileValidator.CreateBackupAsync(tmpFile, CancellationToken.None);

            Assert.IsTrue(File.Exists(backupPath));
            var backupContent = await File.ReadAllTextAsync(backupPath);
            Assert.AreEqual(content, backupContent);

            // Cleanup
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
    public void CreateHeader_ContainsRequiredLines()
    {
        var header = GroupsFileValidator.CreateHeader();

        // Verify the header has the expected number of lines (5: 3 header lines + version line + empty line)
        Assert.IsGreaterThanOrEqualTo(header.Length, 5);

        // Verify the content of the header using key phrases (avoid coupling to private constants)
        StringAssert.Contains(header[0], "DROP list");
        StringAssert.Contains(header[0], "KEEP");
        StringAssert.Contains(header[1], "Lines without");
        StringAssert.Contains(header[1], "DROPPED");
        StringAssert.Contains(header[2], "New groups");
        StringAssert.Contains(header[2], "##");

        // Verify the version line dynamically
        var currentVersion = GroupsFileValidator.GetCurrentVersion();
        var versionLine = header[3];
        StringAssert.Contains(versionLine, currentVersion);

        // Verify formatting: all non-empty lines end with the marker
        foreach (var line in header)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                StringAssert.EndsWith(line, " ######");
            }
        }

        // The version line should be padded to 88 characters
        Assert.AreEqual(88, versionLine.Length);
    }

    [TestMethod]
    public void CreateHeader_ContainsCurrentVersion()
    {
        var header = GroupsFileValidator.CreateHeader();
        var currentVersion = GroupsFileValidator.GetCurrentVersion();

        var versionLine = Array.Find(header, l => l.Contains("Created with iptv version"));
        Assert.IsNotNull(versionLine);
        StringAssert.Contains(versionLine, currentVersion);
    }

    [TestMethod]
    public void GetCurrentVersion_ReturnsValidVersion()
    {
        var version = GroupsFileValidator.GetCurrentVersion();

        Assert.IsFalse(string.IsNullOrEmpty(version));
        StringAssert.Contains(version, ".");
        
        var parts = version.Split('.');
        Assert.HasCount(3, parts, $"Version should have 3 parts (Major.Minor.Build) but got: {version}");
        Assert.IsTrue(int.TryParse(parts[0], out _), $"Major version '{parts[0]}' should be numeric");
        Assert.IsTrue(int.TryParse(parts[1], out _), $"Minor version '{parts[1]}' should be numeric");
        Assert.IsTrue(int.TryParse(parts[2], out _), $"Build version '{parts[2]}' should be numeric");
    }
}

