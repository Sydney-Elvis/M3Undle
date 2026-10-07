using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace M3Undle.Web.Tests.Notifications;

/// <summary>A real, fully migrated SQLite database so unique indexes and foreign keys are the production ones.</summary>
internal sealed class MigratedDatabase : IAsyncDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly string? _filePath;
    private readonly bool _ownsFile;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    private MigratedDatabase(SqliteConnection? connection, string? filePath, bool ownsFile, DbContextOptions<ApplicationDbContext> options)
    {
        _connection = connection;
        _filePath = filePath;
        _ownsFile = ownsFile;
        _options = options;
    }

    public string? ConnectionString => _filePath is null ? null : $"Data Source={_filePath};Foreign Keys=True";

    public SqliteConnection? SharedConnection => _connection;

    /// <summary>One shared in-memory connection (single-threaded tests) or a file with a connection per context (concurrency tests).</summary>
    public static async Task<MigratedDatabase> CreateAsync(string dataSource = ":memory:", bool connectionPerContext = false)
    {
        DbContextOptions<ApplicationDbContext> Options(Action<DbContextOptionsBuilder<ApplicationDbContext>> use)
        {
            var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
            use(builder);
            return builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)).Options;
        }

        MigratedDatabase database;
        if (connectionPerContext)
        {
            var owns = dataSource == ":memory:";
            var path = owns ? Path.Combine(Path.GetTempPath(), $"notif-{Guid.NewGuid():N}.db") : dataSource;
            var connectionString = $"Data Source={path};Foreign Keys=True";
            database = new MigratedDatabase(null, path, owns, Options(o => o.UseSqlite(connectionString)));
        }
        else
        {
            var connection = new SqliteConnection($"Data Source={dataSource};Foreign Keys=True");
            await connection.OpenAsync();
            database = new MigratedDatabase(connection, null, false, Options(o => o.UseSqlite(connection)));
        }

        await using var db = database.CreateDbContext();
        await db.Database.MigrateAsync();
        return database;
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    public DbContextOptions<ApplicationDbContext> Options => _options;

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        if (_filePath is not null)
        {
            SqliteConnection.ClearAllPools();
            if (_ownsFile)
                foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_filePath)!, Path.GetFileName(_filePath) + "*"))
                File.Delete(file);
        }
    }
}

internal static class NotificationTestFactories
{
    public static M3Undle.Web.Application.Notifications.EpgCoverageFacts Facts(
        ApplicationDbContext db,
        M3Undle.Web.Application.Notifications.NotificationRuntimeState? state = null,
        string? dataDirectory = null,
        TimeProvider? timeProvider = null)
    {
        var dir = dataDirectory ?? Path.Combine(Path.GetTempPath(), "m3undle-notif-tests");
        var paths = new M3Undle.Web.Application.RuntimePaths(
            DataDirectory: dir,
            DatabasePath: Path.Combine(dir, "m3undle.db"),
            DatabaseConnectionString: "Data Source=:memory:",
            LogDirectory: dir,
            SnapshotDirectory: dir);
        return new M3Undle.Web.Application.Notifications.EpgCoverageFacts(
            db,
            state ?? new M3Undle.Web.Application.Notifications.NotificationRuntimeState(),
            new M3Undle.Core.Epg.XmltvParser(),
            paths,
            timeProvider ?? TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<M3Undle.Web.Application.Notifications.EpgCoverageFacts>.Instance);
    }
}

internal static class NotificationPolicyHelper
{
    /// <summary>Seeds notification rows, makes the Matrix method enabled and verified, turns sending on, and routes the keys to it.</summary>
    public static async Task EnableAsync(ApplicationDbContext db, params string[] routedKeys)
    {
        var now = DateTime.UtcNow;
        await M3Undle.Web.Application.Notifications.NotificationConfigurationService.EnsureSeededAsync(db, now, CancellationToken.None);

        var settings = await db.NotificationSettings.SingleAsync();
        settings.SendingEnabled = true;
        settings.ActivationEpoch++;

        var destination = await db.NotificationDestinations.Include(d => d.Matrix).SingleAsync(d => d.Kind == M3Undle.Web.Data.Entities.NotificationProviderKinds.Matrix);
        destination.Matrix!.HomeserverUrl = "https://matrix.example.org";
        destination.Matrix.RoomId = "!room:example.org";
        destination.Matrix.AccessTokenEncrypted = "encrypted";
        destination.Enabled = true;
        destination.VerifiedRevision = destination.ConfigRevision;
        destination.VerificationStatus = M3Undle.Web.Data.Entities.NotificationVerificationStates.Verified;

        foreach (var key in routedKeys)
        {
            var route = await db.NotificationRoutes.SingleAsync(r => r.NotificationKey == key);
            route.DestinationId = destination.DestinationId;
            route.Revision++;
        }

        await db.SaveChangesAsync();
    }
}
