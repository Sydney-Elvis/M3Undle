using System.Text.Json;

namespace M3Undle.Web.Application.Backup;

/// <summary>
/// Reads and writes the restore-state marker file directly under the data directory — never
/// under "backups", since that directory's contents are backups themselves, not process state.
/// </summary>
public sealed class RestoreStateStore(RuntimePaths runtimePaths)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private string MarkerPath => Path.Combine(runtimePaths.DataDirectory, "restore-state.json");

    public RestoreStateMarker? Read()
    {
        if (!File.Exists(MarkerPath))
            return null;

        var json = File.ReadAllText(MarkerPath);
        return JsonSerializer.Deserialize<RestoreStateMarker>(json, JsonOptions);
    }

    public void Write(RestoreStateMarker marker)
    {
        Directory.CreateDirectory(runtimePaths.DataDirectory);
        var tempPath = MarkerPath + ".tmp";
        File.WriteAllBytes(tempPath, JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions));
        File.Move(tempPath, MarkerPath, overwrite: true);
    }

    public void Clear()
    {
        if (File.Exists(MarkerPath))
            File.Delete(MarkerPath);
    }
}
