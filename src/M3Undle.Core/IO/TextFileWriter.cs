namespace M3Undle.Core.IO;

public static class TextFileWriter
{
    public static async Task WriteAtomicAsync(string path, IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmpPath = Path.Combine(directory ?? Environment.CurrentDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        
        await using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        await using (var writer = new StreamWriter(stream))
        {
            foreach (var line in lines)
            {
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    public static async Task WriteAtomicTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmpPath = Path.Combine(directory ?? Environment.CurrentDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tmpPath, content, cancellationToken);
        File.Move(tmpPath, path, overwrite: true);
    }
}

