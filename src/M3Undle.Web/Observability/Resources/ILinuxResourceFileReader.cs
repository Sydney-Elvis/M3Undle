namespace M3Undle.Web.Observability.Resources;

// Thin abstraction over the handful of Linux pseudo-files CPU facts read, so the parsers can
// be unit-tested against synthetic content without a real Linux host.
public interface ILinuxResourceFileReader
{
    string? TryReadAllText(string path);
}

public sealed class LinuxResourceFileReader : ILinuxResourceFileReader
{
    public string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
