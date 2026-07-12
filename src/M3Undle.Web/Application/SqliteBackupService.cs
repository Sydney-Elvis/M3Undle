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
        var backupPath = Path.Combine(backupDir, $"m3undle-{DateTime.UtcNow:yyyyMMddHHmmss}.db");

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = db.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            // VACUUM INTO does not reliably support bound parameters for its filename argument
            // (SQLite treats VACUUM as a utility statement, not ordinary DML) — the path is
            // generated entirely by this method (fixed data dir + timestamp), never user input,
            // so an escaped string literal is safe here.
            command.CommandText = $"VACUUM INTO '{backupPath.Replace("'", "''")}'";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        logger.LogInformation("Database backup created at {BackupPath} before encryption key rotation.", backupPath);
        return backupPath;
    }
}
