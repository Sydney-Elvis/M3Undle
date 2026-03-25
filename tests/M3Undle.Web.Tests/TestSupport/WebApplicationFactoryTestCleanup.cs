using Microsoft.Data.Sqlite;

namespace M3Undle.Web.Tests.TestSupport;

internal static class WebApplicationFactoryTestCleanup
{
    public static string CreateSqliteConnectionString(string tempDataDir)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(tempDataDir, "m3undle.db"),
            Pooling = false,
        };

        return builder.ToString();
    }

    public static async Task DeleteDirectoryWhenUnlockedAsync(string tempDataDir)
    {
        if (!Directory.Exists(tempDataDir))
            return;

        SqliteConnection.ClearAllPools();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(tempDataDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    throw;

                SqliteConnection.ClearAllPools();
                await Task.Delay(100);
            }
        }
    }
}
