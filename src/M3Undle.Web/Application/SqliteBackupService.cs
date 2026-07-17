using M3Undle.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace M3Undle.Web.Application;

/// <summary>
/// Creates point-in-time SQLite backups via VACUUM INTO. Must run with no open
/// transaction on the connection — SQLite disallows VACUUM while a transaction is active.
/// </summary>
public sealed class SqliteBackupService(ApplicationDbContext db, RuntimePaths runtimePaths, ILogger<SqliteBackupService> logger)
{
    public async Task<string> BackupAsync(CancellationToken cancellationToken)
    {
        var backupDir = Path.Combine(runtimePaths.DataDirectory, "backups");
        Directory.CreateDirectory(backupDir);
        // Millisecond precision plus a short random suffix — two rotate calls landing in the
        // same second (or even the same millisecond) must not collide on the backup filename.
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var backupPath = Path.Combine(backupDir, $"m3undle-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{uniqueSuffix}.db");

        await VacuumIntoAsync(backupPath, cancellationToken);

        logger.LogInformation("Database backup created at {BackupPath} before encryption key rotation.", backupPath);
        return backupPath;
    }

    /// <summary>
    /// Runs VACUUM INTO against an arbitrary destination path — the primitive every
    /// point-in-time copy (key-rotation safety copy, portable backup, restore rollback
    /// checkpoint) is built on. The destination must never be derived from user input: SQLite
    /// treats VACUUM as a utility statement, not ordinary DML, so its filename argument cannot
    /// reliably be passed as a bound parameter.
    /// </summary>
    public async Task VacuumIntoAsync(string destinationPath, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = $"VACUUM INTO '{destinationPath.Replace("'", "''")}'";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
