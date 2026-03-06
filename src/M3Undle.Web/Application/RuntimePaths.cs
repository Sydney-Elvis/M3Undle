using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

namespace M3Undle.Web.Application;

public sealed record RuntimePaths(
    string DataDirectory,
    string DatabasePath,
    string DatabaseConnectionString,
    string LogDirectory,
    string SnapshotDirectory)
{
    private const string AppFolderName = "M3Undle";
    private const string DefaultDatabaseFile = "m3undle.db";
    private const string DefaultLogDirectory = "logs";
    private const string DefaultSnapshotDirectory = "snapshots";

    public static RuntimePaths Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var perUserDataDirectory = ResolvePerUserDataDirectory();
        var dataDirectory = ResolveDataDirectory(configuration, environment);
        dataDirectory = EnsureDirectoryExists(dataDirectory, perUserDataDirectory);

        var connectionString = ResolveDatabaseConnectionString(configuration, dataDirectory, out var databasePath);
        var logDirectory = EnsureDirectoryExists(ResolveDirectory(
            configuredPath: configuration["M3Undle:Logging:LogDirectory"],
            dataDirectory: dataDirectory,
            defaultRelativePath: DefaultLogDirectory), Path.Combine(dataDirectory, DefaultLogDirectory));
        var snapshotDirectory = EnsureDirectoryExists(ResolveDirectory(
            configuredPath: configuration["M3Undle:Snapshot:Directory"],
            dataDirectory: dataDirectory,
            defaultRelativePath: DefaultSnapshotDirectory), Path.Combine(dataDirectory, DefaultSnapshotDirectory));

        return new RuntimePaths(
            DataDirectory: dataDirectory,
            DatabasePath: databasePath,
            DatabaseConnectionString: connectionString,
            LogDirectory: logDirectory,
            SnapshotDirectory: snapshotDirectory);
    }

    public static string ResolveDirectory(string? configuredPath, string dataDirectory, string defaultRelativePath)
    {
        var value = string.IsNullOrWhiteSpace(configuredPath) ? defaultRelativePath : configuredPath.Trim();
        if (!Path.IsPathRooted(value))
        {
            value = Path.Combine(dataDirectory, value);
        }

        return Path.GetFullPath(value);
    }

    private static string ResolveDatabaseConnectionString(IConfiguration configuration, string dataDirectory, out string databasePath)
    {
        var raw = configuration.GetConnectionString("DefaultConnection");
        var builder = string.IsNullOrWhiteSpace(raw)
            ? new SqliteConnectionStringBuilder()
            : new SqliteConnectionStringBuilder(raw);

        var source = string.IsNullOrWhiteSpace(builder.DataSource) ? DefaultDatabaseFile : builder.DataSource;
        if (!Path.IsPathRooted(source))
        {
            source = Path.Combine(dataDirectory, source);
        }

        databasePath = Path.GetFullPath(source);
        builder.DataSource = databasePath;
        return builder.ToString();
    }

    private static string ResolveDataDirectory(IConfiguration configuration, IHostEnvironment environment)
    {
        var explicitDirectory =
            Environment.GetEnvironmentVariable("M3UNDLE_DATA_DIR")
            ?? configuration["M3Undle:Paths:DataDirectory"];

        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            var candidate = explicitDirectory.Trim();
            if (!Path.IsPathRooted(candidate))
            {
                candidate = Path.Combine(environment.ContentRootPath, candidate);
            }

            return Path.GetFullPath(candidate);
        }

        var inContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (inContainer)
        {
            return "/data";
        }

        if (environment.IsDevelopment())
        {
            return ResolvePerUserDataDirectory();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
            {
                return Path.Combine(programData, AppFolderName);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "/Library/Application Support/M3Undle";
        }
        else
        {
            return "/var/lib/m3undle";
        }

        return ResolvePerUserDataDirectory();
    }

    private static string ResolvePerUserDataDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            return Path.Combine(localAppData, AppFolderName);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, ".local", "share", AppFolderName);
        }

        return Path.Combine(Path.GetTempPath(), AppFolderName);
    }

    private static string EnsureDirectoryExists(string preferredPath, string fallbackPath)
    {
        try
        {
            Directory.CreateDirectory(preferredPath);
            return preferredPath;
        }
        catch when (!PathsEqual(preferredPath, fallbackPath))
        {
            Directory.CreateDirectory(fallbackPath);
            return fallbackPath;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }
}
