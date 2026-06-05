using M3Undle.Web.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace M3Undle.Web.Tests.Persistence;

[TestClass]
public sealed class MigrationBaselineTests
{
    [TestMethod]
    public async Task ApplicationDbContext_HasSingleAlphaMigrationBaseline()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);

        var migrations = db.Database.GetService<IMigrationsAssembly>().Migrations;
        Assert.AreEqual(1, migrations.Count);
        Assert.IsTrue(migrations.Single().Key.EndsWith("_Alpha_Schema", StringComparison.Ordinal));

        await db.Database.MigrateAsync();

        await using var historyCommand = connection.CreateCommand();
        historyCommand.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsHistory\";";
        Assert.AreEqual(1, Convert.ToInt32(await historyCommand.ExecuteScalarAsync()));
    }

    private static ApplicationDbContext CreateDb(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new ApplicationDbContext(options);
    }
}
