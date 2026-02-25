using System.Reflection;

namespace M3Undle.Core.IO;

public static class GroupsFileValidator
{
    private const string HeaderLine1 = "######  This is a DROP list. Put a '#' in front of any group you want to KEEP.    ######";
    private const string HeaderLine2 = "######  Lines without '#' will be DROPPED. Blank lines are ignored.               ######";
    private const string HeaderLine3 = "######  New groups are marked with '##' for easy identification.                  ######";
    private const string VersionPrefix = "######  Created with bndl version ";
    
    public static string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
    }

    public static async Task<ValidationResult> ValidateFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return new ValidationResult(true, null, null);
        }

        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        
        // Check for required headers - be flexible with whitespace and trailing ######
        // Look for the key parts of the header lines
        var hasHeader1 = lines.Any(l => 
            l.TrimStart().StartsWith("######") && 
            l.Contains("DROP list") && 
            l.Contains("KEEP"));
        var hasHeader2 = lines.Any(l => 
            l.TrimStart().StartsWith("######") && 
            l.Contains("Lines without") && 
            l.Contains("DROPPED"));
        
        if (!hasHeader1 && !hasHeader2)
        {
            return new ValidationResult(false, null, "File does not appear to be a valid groups file (missing header lines)");
        }

        // Check version
        string? fileVersion = null;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith(VersionPrefix, StringComparison.Ordinal))
            {
                var versionText = line.TrimStart()[VersionPrefix.Length..].Split('#')[0].Trim();
                fileVersion = versionText;
                break;
            }
        }

        // If no version is found, the file is from an older version or invalid
        if (fileVersion == null)
        {
            return new ValidationResult(false, null, "File does not contain version information (created with older version or invalid)");
        }

        var currentVersion = GetCurrentVersion();
        var currentMajor = currentVersion.Split('.')[0];
        var fileMajor = fileVersion.Split('.')[0];
        
        if (fileMajor != currentMajor)
        {
            return new ValidationResult(
                false,
                fileVersion,
                $"File was created with bndl version {fileVersion} but current version is {currentVersion} (major version mismatch)");
        }

        return new ValidationResult(true, fileVersion, null);
    }

    public static string CreateBackupPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? string.Empty;
        var fileName = Path.GetFileName(originalPath);
        var backupPath = Path.Combine(directory, $"{fileName}.bak");
        
        // If .bak exists, try .bak1, .bak2, etc.
        if (File.Exists(backupPath))
        {
            int counter = 1;
            while (File.Exists(Path.Combine(directory, $"{fileName}.bak{counter}")))
            {
                counter++;
                if (counter > 999) // Prevent infinite loop
                {
                    throw new CliException($"Too many backup files exist for {originalPath}", ExitCodes.IoError);
                }
            }
            backupPath = Path.Combine(directory, $"{fileName}.bak{counter}");
        }
        
        return backupPath;
    }

    public static async Task CreateBackupAsync(string originalPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(originalPath))
        {
            return;
        }

        var backupPath = CreateBackupPath(originalPath);
        await using var sourceStream = new FileStream(originalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        await using var destStream = new FileStream(backupPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await sourceStream.CopyToAsync(destStream, cancellationToken);
    }

    public static string[] CreateHeader()
    {
        var currentVersion = GetCurrentVersion();
        var versionLine = $"{VersionPrefix}{currentVersion}";
        // Pad so the line (including trailing ' ######') is always 88 characters
        const int totalLength = 88;
        const string trailer = " ######";
        
        var paddedVersionLine = versionLine.PadRight(totalLength - trailer.Length) + trailer;
        return
        [
            HeaderLine1,
            HeaderLine2,
            HeaderLine3,
            paddedVersionLine,
            string.Empty
        ];
    }

    public sealed record ValidationResult(bool IsValid, string? FileVersion, string? ErrorMessage);
}

