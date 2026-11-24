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
        var rollbackDatabase = Path.Join(directory, "legacy-wrong-key.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);

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

            using (new SqliteFamilyStore(rollbackDatabase, key))
            {
            }
            using (var legacy = new SqliteConnection(
                new SqliteConnectionStringBuilder {
                    DataSource = rollbackDatabase, Pooling = false }.ToString()))
            {
                legacy.Open();
                using var downgrade = legacy.CreateCommand();
                downgrade.CommandText = """
                    DROP TABLE relationship_proposals;
                    DROP TABLE relationships;
                    ALTER TABLE audit DROP COLUMN created_utc;
                    """;
                downgrade.ExecuteNonQuery();
            }
            Expect<InvalidDataException>(
                () => new SqliteFamilyStore(rollbackDatabase, wrongKey),
                "wrong database key rejects legacy open");
            using (var rolledBack = new SqliteConnection(
                new SqliteConnectionStringBuilder {
                    DataSource = rollbackDatabase, Pooling = false }.ToString()))
            {
                rolledBack.Open();
                using var inspect = rolledBack.CreateCommand();
                inspect.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type='table' AND name IN ('relationships',
                        'relationship_proposals');
                    """;
                Check(Convert.ToInt64(inspect.ExecuteScalar()) == 0 &&
                    !HasAuditTimestamp(rolledBack),
                    "wrong-key open rolls back every schema change");
            }
            using (var reopened = new SqliteFamilyStore(rollbackDatabase, key))
            using (var migrated = new SqliteConnection(
                new SqliteConnectionStringBuilder {
                    DataSource = rollbackDatabase, Pooling = false }.ToString()))
            {
                migrated.Open();
                using var inspect = migrated.CreateCommand();
                inspect.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type='table' AND name IN ('relationships',
                        'relationship_proposals');
                    """;
                Check(Convert.ToInt64(inspect.ExecuteScalar()) == 2 &&
                    HasAuditTimestamp(migrated),
                    "valid key completes deferred schema migration");
            }
            Console.WriteLine("PASS concurrent SQLite legacy audit schema migration");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(wrongKey);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Check(bool accepted, string reason)
    {
        if (!accepted) throw new InvalidOperationException(reason);
    }

    private static bool HasAuditTimestamp(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(audit);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1) == "created_utc") return true;
        return false;
    }

    private static void Expect<T>(Action action, string reason) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(reason);
    }
}
