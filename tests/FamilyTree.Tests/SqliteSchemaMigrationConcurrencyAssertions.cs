using System.Security.Cryptography;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

internal static class SqliteSchemaMigrationConcurrencyAssertions
{
    internal static async Task RunAsync()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "familytree-schema-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Join(directory, "legacy.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);

        try
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder {
                    DataSource = database, Pooling = false }.ToString()))
            {
                connection.Open();
                using var createAudit = connection.CreateCommand();
                createAudit.CommandText = """
                    CREATE TABLE audit (
                        seq INTEGER PRIMARY KEY AUTOINCREMENT,
                        family_id TEXT NOT NULL, subject_id TEXT NOT NULL,
                        reviewer TEXT NOT NULL, action TEXT NOT NULL
                    );
                    """;
                createAudit.ExecuteNonQuery();
            }

            using var start = new Barrier(2);
            Task<SqliteFamilyStore> OpenStore() => Task.Run(() =>
            {
                if (!start.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Concurrent legacy store open timed out.");
                return new SqliteFamilyStore(database, key);
            });
            var firstOpen = OpenStore();
            var secondOpen = OpenStore();
            using var firstStore = await firstOpen;
            using var secondStore = await secondOpen;

            using var verify = new SqliteConnection(
                new SqliteConnectionStringBuilder {
                    DataSource = database, Pooling = false }.ToString());
            verify.Open();
            using var columns = verify.CreateCommand();
            columns.CommandText = "PRAGMA table_info(audit);";
            using var reader = columns.ExecuteReader();
            var hasTimestamp = false;
            while (reader.Read())
                hasTimestamp |= reader.GetString(1) == "created_utc";
            Check(hasTimestamp, "legacy audit timestamp migration is durable");
            Console.WriteLine("PASS concurrent SQLite legacy audit schema migration");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Check(bool accepted, string reason)
    {
        if (!accepted) throw new InvalidOperationException(reason);
    }
}
