using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && args[0] == "--verify-restored")
    return VerifyRestoredProcess(args[1..]);
if (args.Length == 2 && args[0] == "--check-coverage")
    return CheckRecoveryCoverage(args[1]);

RunRecoveryAssertions();
Console.WriteLine("PASS SQLite recovery, WAL, authentication, rollback, path safety and restart tests");
return 0;

static void RunRecoveryAssertions()
{
    AssertRecoveryValidationBranches();
    var directory = Path.Combine(Path.GetTempPath(), "familytree-recovery-" + Guid.NewGuid().ToString("N"));
    var restoreDirectory = Path.Combine(directory, "restored");
    Directory.CreateDirectory(directory);
    Directory.CreateDirectory(restoreDirectory);
    var databasePath = Path.Combine(directory, "synthetic.sqlite");
    var backupPath = Path.Combine(directory, "synthetic.backup");
    var restoredPath = Path.Combine(restoreDirectory, "recovered.sqlite");
    var externalBackupKeyPath = Environment.GetEnvironmentVariable(
        "FAMILYTREE_RECOVERY_TEST_KEY_FILE");
    var usesExternalBackupKey = !string.IsNullOrWhiteSpace(externalBackupKeyPath);
    var keyPath = usesExternalBackupKey
        ? Path.GetFullPath(externalBackupKeyPath!)
        : Path.Combine(directory, "recovery.key");
    var familyKeyPath = Path.Combine(directory, "family.key");
    var identityKeyPath = Path.Combine(directory, "identity.key");
    var familyKey = RandomNumberGenerator.GetBytes(32);
    var identityKey = RandomNumberGenerator.GetBytes(32);
    var backupKey = usesExternalBackupKey
        ? File.ReadAllBytes(keyPath)
        : RandomNumberGenerator.GetBytes(32);
    var wrongKey = RandomNumberGenerator.GetBytes(32);
    var familyId = Guid.NewGuid();
    var adminId = Guid.NewGuid();
    var memberId = Guid.NewGuid();
    var revokedMemberId = Guid.NewGuid();
    var branchId = Guid.NewGuid();
    var person = new Person(Guid.NewGuid(), "Fictional Cypress Person");
    var auditCount = 0L;

    try
    {
        Check(!familyKey.SequenceEqual(identityKey) && !familyKey.SequenceEqual(backupKey) &&
              !identityKey.SequenceEqual(backupKey), "database, identity and backup keys are separate");
        if (!usesExternalBackupKey)
            File.WriteAllBytes(keyPath, backupKey);
        File.WriteAllBytes(familyKeyPath, familyKey);
        File.WriteAllBytes(identityKeyPath, identityKey);
        using (var store = new SqliteFamilyStore(databasePath, familyKey))
        {
            store.CreateFictionalFamily(familyId, adminId);
            JoinMember(store, familyId, adminId, memberId);
            JoinMember(store, familyId, adminId, revokedMemberId);
            var proposal = store.ProposePerson(familyId, memberId, person);
            store.ReviewPerson(familyId, adminId, proposal, approve: true);

            using var registry = new SqlitePrincipalRegistry(databasePath, identityKey);
            var principal = registry.EnrollVerifiedSubject(
                "google", "https://accounts.google.com", "synthetic-subject");
            Check(registry.FindVerifiedSubject(
                "google", "https://accounts.google.com", "synthetic-subject") == principal,
                "identity registry is backed up");

            var grants = new SqliteGraphGrantStore(databasePath);
            grants.GrantPerson(familyId, branchId, adminId, memberId, person.Id);
            grants.GrantPerson(familyId, branchId, adminId, revokedMemberId, person.Id);
            grants.RevokePerson(familyId, branchId, adminId, revokedMemberId, person.Id);
            Check(grants.ReadVisiblePersonIds(familyId, branchId, memberId).Contains(person.Id),
                "active member's visibility grant exists before backup");
            Check(grants.ReadVisiblePersonIds(familyId, branchId, revokedMemberId).Count == 0,
                "revoked visibility grant is absent before backup");

            store.RevokeMember(familyId, adminId, revokedMemberId);
            auditCount = store.CountAudit(familyId, adminId);
            Check(auditCount >= 4, "membership and review actions are audited before backup");
        }

        using (var wal = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString()))
        {
            wal.Open();
            using (var command = wal.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                command.ExecuteNonQuery();
            }
            using (var command = wal.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO audit(family_id,subject_id,reviewer,action)
                    VALUES($family,$subject,$admin,'synthetic-wal-commit');
                    """;
                command.Parameters.AddWithValue("$family", familyId.ToString("D"));
                command.Parameters.AddWithValue("$subject", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$admin", adminId.ToString("D"));
                command.ExecuteNonQuery();
            }
            using var store = new SqliteFamilyStore(databasePath, familyKey);
            auditCount = store.CountAudit(familyId, adminId);
            Check(File.Exists(databasePath + "-wal") && new FileInfo(databasePath + "-wal").Length > 0,
                "committed WAL frames exist while the database is open");
            SqliteRecovery.CreateBackup(databasePath, backupPath, backupKey);
            Check(File.Exists(databasePath + "-wal") && new FileInfo(databasePath + "-wal").Length > 0,
                "backup does not require copying or checkpointing live WAL files");
        }

        var snapshot = File.ReadAllBytes(backupPath);
        Check(!Contains(snapshot, Encoding.UTF8.GetBytes(person.DisplayName)),
            "encrypted backup contains no plaintext synthetic record");
        Check(!Contains(snapshot, backupKey), "backup artifact does not contain its external key");
        SqliteRecovery.Restore(backupPath, restoredPath, backupKey);
        Check(File.Exists(restoredPath), "restore publishes a completed isolated database");
        Check(!Directory.EnumerateFiles(restoreDirectory, ".ft-recovery-*").Any(),
            "successful restore removes temporary files");

        VerifyRestoredInFreshProcess(restoredPath, familyKeyPath, identityKeyPath,
            familyId, adminId, memberId, revokedMemberId, branchId, person.Id, auditCount);
        TestCliIntegration(databasePath, keyPath, familyKey, familyId, memberId, person);

        var preservedTarget = Path.Combine(restoreDirectory, "existing.sqlite");
        var originalTarget = Encoding.UTF8.GetBytes("synthetic rollback sentinel");
        File.WriteAllBytes(preservedTarget, originalTarget);
        Expect<IOException>(() => SqliteRecovery.Restore(backupPath, preservedTarget, backupKey),
            "restore refuses to replace an existing database");
        Check(File.ReadAllBytes(preservedTarget).SequenceEqual(originalTarget),
            "failed restore leaves its prior destination unchanged");

        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(databasePath, databasePath, backupKey),
            "backup cannot replace the source database");
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup("", backupPath, backupKey),
            "backup rejects an empty source path");
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(" ", backupPath, backupKey),
            "backup rejects a whitespace source path");
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(null!, backupPath, backupKey),
            "backup rejects a missing source path");
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(
            "relative.sqlite", backupPath, backupKey),
            "backup rejects a relative source path");
        Expect<ArgumentException>(() => SqliteRecovery.Restore(backupPath, "relative.sqlite", backupKey),
            "restore rejects a relative destination path");
        Expect<DirectoryNotFoundException>(() => SqliteRecovery.CreateBackup(
            databasePath, Path.Combine(directory, "missing", "backup.sqlite"), backupKey),
            "backup requires an existing output directory");
        Expect<IOException>(() => SqliteRecovery.CreateBackup(
            databasePath, directory, backupKey),
            "backup rejects a directory as its output");
        Expect<DirectoryNotFoundException>(() => SqliteRecovery.Restore(
            backupPath, Path.Combine(directory, "missing", "restored.sqlite"), backupKey),
            "restore requires an existing target directory");
        Expect<IOException>(() => SqliteRecovery.Restore(
            backupPath, restoreDirectory, backupKey),
            "restore rejects a directory as its target");
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(
            databasePath, databasePath.ToUpperInvariant(), backupKey),
            "case-variant path aliases are rejected conservatively");
        var composedPath = Path.Combine(directory, "synthetic-\u00e9.sqlite");
        var decomposedPath = Path.Combine(directory, "synthetic-e\u0301.sqlite");
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(
            composedPath, decomposedPath, backupKey),
            "canonically equivalent Unicode path aliases are rejected");
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(
            databasePath, databasePath + "-wal", backupKey),
            "backup cannot replace the source WAL sidecar");
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(
            databasePath, databasePath + "-WAL", backupKey),
            "case-variant WAL sidecar aliases are rejected");
        Expect<ArgumentException>(() => SqliteRecovery.Restore(backupPath, backupPath, backupKey),
            "restore cannot replace its encrypted source");
        var sidecarTarget = Path.Combine(restoreDirectory, "sidecar.sqlite");
        File.WriteAllBytes(sidecarTarget + "-wal", [1]);
        try
        {
            Expect<IOException>(() => SqliteRecovery.Restore(backupPath, sidecarTarget, backupKey),
                "restore rejects a destination with a SQLite sidecar");
        }
        finally
        {
            File.Delete(sidecarTarget + "-wal");
        }
        TestRestoreDestinationRace(backupPath, backupKey, restoreDirectory, sidecar: false);
        TestRestoreDestinationRace(backupPath, backupKey, restoreDirectory, sidecar: true);
        Expect<IOException>(() => SqliteRecovery.CreateBackup(databasePath, keyPath, backupKey),
            "backup never overwrites an existing key or other file");
        Check(File.ReadAllBytes(keyPath).SequenceEqual(backupKey),
            "existing backup key remains unchanged");
        TestMalformedSchemas(databasePath, directory, restoreDirectory, backupKey);
        TestLegacySchema(databasePath, directory, restoreDirectory, backupKey, familyKey,
            familyId, memberId, person.Id);
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(databasePath, backupPath, new byte[8]),
            "backup requires a 256-bit external key");
        Expect<ArgumentException>(() => SqliteRecovery.Restore(backupPath, restoredPath, new byte[8]),
            "restore requires a 256-bit external key");

        var wrongKeyTarget = Path.Combine(restoreDirectory, "wrong-key.sqlite");
        Expect<InvalidDataException>(() => SqliteRecovery.Restore(backupPath, wrongKeyTarget, wrongKey),
            "wrong backup key fails authentication");
        Check(!File.Exists(wrongKeyTarget), "wrong key never creates a restore destination");

        var tamperedPath = Path.Combine(directory, "tampered.backup");
        var tampered = (byte[])snapshot.Clone();
        tampered[^1] ^= 0x40;
        File.WriteAllBytes(tamperedPath, tampered);
        var tamperedTarget = Path.Combine(restoreDirectory, "tampered.sqlite");
        Expect<InvalidDataException>(() => SqliteRecovery.Restore(tamperedPath, tamperedTarget, backupKey),
            "tampered ciphertext fails authentication");
        Check(!File.Exists(tamperedTarget), "tampered backup never creates a restore destination");

        var oversizedPath = Path.Combine(directory, "oversized.backup");
        using (var oversized = new FileStream(oversizedPath, FileMode.CreateNew, FileAccess.Write))
            oversized.SetLength(1024L * 1024 * 1024 + 37);
        var oversizedTarget = Path.Combine(restoreDirectory, "oversized.sqlite");
        Expect<InvalidDataException>(() => SqliteRecovery.Restore(oversizedPath, oversizedTarget, backupKey),
            "oversized encrypted input is rejected before allocation");
        Check(!File.Exists(oversizedTarget), "oversized input is never published");

        var partialPath = Path.Combine(directory, "partial.backup");
        File.WriteAllBytes(partialPath, snapshot[..Math.Min(snapshot.Length, 24)]);
        Expect<InvalidDataException>(() => SqliteRecovery.Restore(
            partialPath, Path.Combine(restoreDirectory, "partial.sqlite"), backupKey),
            "incomplete encrypted output fails closed");

        var corruptDatabase = (byte[])DecryptTestSnapshot(snapshot, backupKey);
        try
        {
            corruptDatabase[0] ^= 0x40;
            var corruptPath = Path.Combine(directory, "authenticated-corrupt.backup");
            File.WriteAllBytes(corruptPath, EncryptTestSnapshot(corruptDatabase, backupKey));
            Expect<InvalidDataException>(() => SqliteRecovery.Restore(
                corruptPath, Path.Combine(restoreDirectory, "corrupt.sqlite"), backupKey),
                "authenticated but corrupt SQLite data fails integrity validation");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(corruptDatabase);
        }

        var partialOutput = Path.Combine(directory, "preserved.backup");
        var originalOutput = Encoding.UTF8.GetBytes("previous encrypted output");
        File.WriteAllBytes(partialOutput, originalOutput);
        Expect<FileNotFoundException>(() => SqliteRecovery.CreateBackup(
            Path.Combine(directory, "not-a-database.sqlite"), partialOutput, backupKey),
            "failed backup does not publish an incomplete output");
        Check(File.ReadAllBytes(partialOutput).SequenceEqual(originalOutput),
            "failed backup preserves the previous output");

        Check(!Directory.EnumerateFiles(directory, ".ft-recovery-*").Any() &&
              !Directory.EnumerateFiles(restoreDirectory, ".ft-recovery-*").Any(),
            "failed operations clean temporary output files");
        Check(!Contains(File.ReadAllBytes(databasePath), Encoding.UTF8.GetBytes(person.DisplayName)),
            "source database fixture itself contains no plaintext synthetic name");

        TestSymlinkRejection(directory, databasePath, backupKey, backupPath, restoreDirectory);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(familyKey);
        CryptographicOperations.ZeroMemory(identityKey);
        CryptographicOperations.ZeroMemory(backupKey);
        CryptographicOperations.ZeroMemory(wrongKey);
        Directory.Delete(directory, recursive: true);
    }
}

static void TestMalformedSchemas(string source, string directory, string restoreDirectory, byte[] key)
{
    var cases = new[]
    {
        (Table: "key_verifier", Old: "envelope BLOB NOT NULL", New: "placeholder BLOB NOT NULL"),
        (Table: "memberships", Old: "CHECK (administrator IN (0,1))", New: ""),
        (Table: "people", Old: "encrypted_name BLOB", New: "encrypted_name\u00a0BLOB"),
        (Table: "person_visibility", Old: "REFERENCES memberships(family_id,principal_id)",
            New: "REFERENCES families(id)")
    };
    for (var index = 0; index < cases.Length; index++)
    {
        var (table, oldText, newText) = cases[index];
        var malformed = Path.Combine(directory, $"malformed-{index}.sqlite");
        File.Copy(source, malformed);
        using (var connection = new SqliteConnection($"Data Source={malformed};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA writable_schema=ON;
                UPDATE sqlite_master SET sql=replace(sql,$old,$new)
                WHERE type='table' AND name=$table AND instr(sql,$old)>0;
                """;
            command.Parameters.AddWithValue("$old", oldText);
            command.Parameters.AddWithValue("$new", newText);
            command.Parameters.AddWithValue("$table", table);
            Check(command.ExecuteNonQuery() == 1, "synthetic schema mutation was applied");
        }
        var output = Path.Combine(directory, $"malformed-{index}.backup");
        Expect<InvalidDataException>(() => SqliteRecovery.CreateBackup(malformed, output, key),
            "backup rejects missing columns and constraints");
        Check(!File.Exists(output), "malformed schema is not backed up");
        File.WriteAllBytes(output, EncryptTestSnapshot(File.ReadAllBytes(malformed), key));
        var target = Path.Combine(restoreDirectory, $"malformed-{index}.sqlite");
        Expect<InvalidDataException>(() => SqliteRecovery.Restore(output, target, key),
            "restore rejects authenticated malformed schemas");
        Check(!File.Exists(target), "malformed schema is not published");
    }

    var placeholders = Path.Combine(directory, "placeholder.sqlite");
    using (var connection = new SqliteConnection($"Data Source={placeholders};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = string.Join("; ", new[]
        {
            "families", "memberships", "invitations", "people", "person_proposals", "audit", "key_verifier"
        }.Select(name => $"CREATE TABLE {name} (placeholder TEXT)"));
        command.ExecuteNonQuery();
    }
    Expect<InvalidDataException>(() => SqliteRecovery.CreateBackup(
        placeholders, Path.Combine(directory, "placeholder.backup"), key),
        "table names alone do not constitute a valid schema");

    foreach (var (table, sql, label) in new[]
             {
                 ("key_verifier", "DELETE FROM key_verifier;", "missing-family"),
                 ("identity_key_verifier", "DELETE FROM identity_key_verifier;", "missing-identity"),
                 ("key_verifier", "UPDATE key_verifier SET envelope=x'';", "invalid-family"),
                 ("key_verifier", "UPDATE key_verifier SET envelope=zeroblob(54);", "truncated-family"),
                 ("identity_key_verifier", "UPDATE identity_key_verifier SET digest='invalid';", "invalid-identity")
             })
    {
        var missingVerifier = Path.Combine(directory, $"{label}.sqlite");
        File.Copy(source, missingVerifier);
        using (var connection = new SqliteConnection($"Data Source={missingVerifier};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            Check(command.ExecuteNonQuery() == 1, $"synthetic {table} verifier was changed");
        }
        var invalidBackup = Path.Combine(directory, $"{label}.backup");
        Expect<InvalidDataException>(() => SqliteRecovery.CreateBackup(missingVerifier, invalidBackup, key),
            "backup rejects missing or malformed key metadata");
        Check(!File.Exists(invalidBackup), "invalid verifier is not backed up");
        File.WriteAllBytes(invalidBackup, EncryptTestSnapshot(File.ReadAllBytes(missingVerifier), key));
        var invalidTarget = Path.Combine(restoreDirectory, $"{label}.sqlite");
        Expect<InvalidDataException>(() => SqliteRecovery.Restore(invalidBackup, invalidTarget, key),
            "restore rejects missing or malformed key metadata");
        Check(!File.Exists(invalidTarget), "invalid verifier is not published");
    }

    var triggerDatabase = Path.Combine(directory, "trigger.sqlite");
    File.Copy(source, triggerDatabase);
    using (var connection = new SqliteConnection($"Data Source={triggerDatabase};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER unexpected AFTER INSERT ON families BEGIN DELETE FROM memberships; END;";
        command.ExecuteNonQuery();
    }
    var triggerBackup = Path.Combine(directory, "trigger.backup");
    Expect<InvalidDataException>(() => SqliteRecovery.CreateBackup(triggerDatabase, triggerBackup, key),
        "backup rejects unexpected triggers");
    File.WriteAllBytes(triggerBackup, EncryptTestSnapshot(File.ReadAllBytes(triggerDatabase), key));
    var triggerTarget = Path.Combine(restoreDirectory, "trigger.sqlite");
    Expect<InvalidDataException>(() => SqliteRecovery.Restore(triggerBackup, triggerTarget, key),
        "restore rejects authenticated unexpected triggers");
    Check(!File.Exists(triggerTarget), "trigger database is not published");

    var indexDatabase = Path.Combine(directory, "index.sqlite");
    File.Copy(source, indexDatabase);
    using (var connection = new SqliteConnection($"Data Source={indexDatabase};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE UNIQUE INDEX unexpected ON families(id, id);";
        command.ExecuteNonQuery();
    }
    var indexBackup = Path.Combine(directory, "index.backup");
    Expect<InvalidDataException>(() => SqliteRecovery.CreateBackup(indexDatabase, indexBackup, key),
        "backup rejects unexpected indexes");
    File.WriteAllBytes(indexBackup, EncryptTestSnapshot(File.ReadAllBytes(indexDatabase), key));
    var indexTarget = Path.Combine(restoreDirectory, "index.sqlite");
    Expect<InvalidDataException>(() => SqliteRecovery.Restore(indexBackup, indexTarget, key),
        "restore rejects authenticated unexpected indexes");
    Check(!File.Exists(indexTarget), "indexed database is not published");
}

static void TestLegacySchema(string source, string directory, string restoreDirectory,
    byte[] backupKey, byte[] familyKey, Guid familyId, Guid memberId, Guid personId)
{
    var legacy = Path.Combine(directory, "legacy.sqlite");
    File.Copy(source, legacy);
    using (var connection = new SqliteConnection($"Data Source={legacy};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE relationship_proposals;
            DROP TABLE relationships;
            ALTER TABLE audit DROP COLUMN created_utc;
            """;
        command.ExecuteNonQuery();
    }
    var backup = Path.Combine(directory, "legacy.backup");
    var restored = Path.Combine(restoreDirectory, "legacy.sqlite");
    SqliteRecovery.CreateBackup(legacy, backup, backupKey);
    SqliteRecovery.Restore(backup, restored, backupKey);
    using var store = new SqliteFamilyStore(restored, familyKey);
    Check(store.ReadPeople(familyId, memberId).Any(person => person.Id == personId),
        "supported legacy snapshot restores and migrates without losing people");
}

static void AssertRecoveryValidationBranches()
{
    using var connection = new SqliteConnection("Data Source=:memory:");
    connection.Open();
    Check(SqliteRecovery.IsDeleteJournalMode("DELETE") &&
          !SqliteRecovery.IsDeleteJournalMode("wal") &&
          !SqliteRecovery.IsDeleteJournalMode(null),
        "journal-mode validation accepts only DELETE");
    SqliteRecovery.EnsureDeleteJournalMode("delete");
    Expect<InvalidDataException>(() => SqliteRecovery.EnsureDeleteJournalMode("wal"),
        "journal-mode enforcement rejects unexpected modes");
    Check(SqliteRecovery.IsSerializedBufferValid(new IntPtr(1), 1) &&
          !SqliteRecovery.IsSerializedBufferValid(IntPtr.Zero, 1) &&
          !SqliteRecovery.IsSerializedBufferValid(new IntPtr(1), 0) &&
          !SqliteRecovery.IsSerializedBufferValid(new IntPtr(1), int.MaxValue),
        "serialized-buffer validation rejects missing and invalid buffers");
    SqliteRecovery.EnsureSerializedBufferValid(new IntPtr(1), 1);
    Expect<InvalidDataException>(() => SqliteRecovery.EnsureSerializedBufferValid(IntPtr.Zero, 1),
        "serialized-buffer enforcement rejects a missing buffer");
    var allocated = SQLitePCL.raw.sqlite3_malloc(1);
    if (allocated != IntPtr.Zero)
        Expect<InvalidDataException>(() => SqliteRecovery.EnsureSerializedBufferValid(allocated, 0),
            "serialized-buffer enforcement releases invalid native allocations");
    Check(SqliteRecovery.IsSnapshotSizeValid(1) &&
          !SqliteRecovery.IsSnapshotSizeValid(0) &&
          !SqliteRecovery.IsSnapshotSizeValid(int.MaxValue),
        "snapshot-size validation enforces positive bounded lengths");
    SqliteRecovery.EnsureSnapshotSizeValid(1);
    Expect<InvalidDataException>(() => SqliteRecovery.EnsureSnapshotSizeValid(0),
        "snapshot-size enforcement rejects empty databases");
    Check(SqliteRecovery.IsIntegrityCheckValid(true, "ok", false) &&
          !SqliteRecovery.IsIntegrityCheckValid(false, null, false) &&
          !SqliteRecovery.IsIntegrityCheckValid(true, "corrupt", false) &&
          !SqliteRecovery.IsIntegrityCheckValid(true, "ok", true),
        "integrity validation rejects absent, failed and multiple results");
    var validResultReads = 1;
    SqliteRecovery.EnsureIntegrityCheckValid(
        () => validResultReads-- > 0, () => "ok");
    Expect<InvalidDataException>(() => SqliteRecovery.EnsureIntegrityCheckValid(
        () => false, () => throw new InvalidOperationException()),
        "integrity enforcement rejects missing results");
    Expect<InvalidDataException>(() => SqliteRecovery.EnsureIntegrityCheckValid(
        () => true, () => "corrupt"),
        "integrity enforcement rejects corrupt results");
    var multipleResults = new Queue<string>(["ok", "extra"]);
    Expect<InvalidDataException>(() => SqliteRecovery.EnsureIntegrityCheckValid(
        () => multipleResults.Count > 0, () => multipleResults.Dequeue()),
        "integrity enforcement rejects multiple results");
    Check(SqliteRecovery.IsForeignKeyCheckClean(false) &&
          !SqliteRecovery.IsForeignKeyCheckClean(true),
        "foreign-key validation rejects reported violations");
    SqliteRecovery.EnsureForeignKeyCheckClean(false);
    Expect<InvalidDataException>(() => SqliteRecovery.EnsureForeignKeyCheckClean(true),
        "foreign-key enforcement rejects reported violations");
    Check(SqliteRecovery.JoinChildPath(Path.GetTempPath(), "safe-child") ==
          Path.Join(Path.GetTempPath(), "safe-child"),
        "recovery path composition accepts a single safe child");
    foreach (var invalidChild in new[]
             {
                 "", ".", "..", Path.GetFullPath("absolute-child"),
                 Path.Join("nested", "child"), "\0"
             })
        Expect<IOException>(() => SqliteRecovery.JoinChildPath(Path.GetTempPath(), invalidChild),
            "recovery path composition rejects unsafe child segments");
    Check(!SqliteRecovery.HasReparseAttributes(false, false, default) &&
          !SqliteRecovery.HasReparseAttributes(true, false, FileAttributes.Normal) &&
          !SqliteRecovery.HasReparseAttributes(false, true, FileAttributes.Directory) &&
          SqliteRecovery.HasReparseAttributes(true, false, FileAttributes.ReparsePoint) &&
          SqliteRecovery.HasReparseAttributes(false, true,
              FileAttributes.Directory | FileAttributes.ReparsePoint),
        "recovery path validation detects reparse attributes on files and directories");
}

static void TestRestoreDestinationRace(
    string backupPath, byte[] backupKey, string restoreDirectory, bool sidecar)
{
    var destination = Path.Combine(restoreDirectory,
        sidecar ? "raced-sidecar.sqlite" : "raced-destination.sqlite");
    var sentinel = Encoding.UTF8.GetBytes("destination race sentinel");
    var racedPath = sidecar ? destination + "-wal" : destination;
    Expect<IOException>(() => SqliteRecovery.Restore(backupPath, destination, backupKey,
        () => File.WriteAllBytes(racedPath, sentinel)),
        "restore rejects a destination collision before publication");
    Check(File.ReadAllBytes(racedPath).SequenceEqual(sentinel) &&
          (sidecar ? !File.Exists(destination) : true),
        sidecar
            ? "restore refuses a sidecar created while staging"
            : "restore refuses a destination created while staging");
    File.Delete(racedPath);
}

static void JoinMember(SqliteFamilyStore store, Guid family, Guid admin, Guid member)
{
    var invitation = store.IssueInvitation(
        family, admin, member, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
    store.AcceptInvitation(family, member, invitation, DateTimeOffset.UtcNow);
    store.ApproveMember(family, admin, member);
}

static void VerifyRestoredInFreshProcess(
    string databasePath, string familyKeyPath, string identityKeyPath,
    Guid family, Guid admin, Guid member, Guid revokedMember, Guid branch,
    Guid person, long auditCount)
{
    var processPath = Environment.ProcessPath ?? throw new InvalidOperationException();
    var start = new ProcessStartInfo
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.FileName = processPath;
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    }
    else
    {
        start.FileName = processPath;
    }
    start.ArgumentList.Add("--verify-restored");
    foreach (var value in new[]
             {
                 databasePath, familyKeyPath, identityKeyPath, family.ToString("D"), admin.ToString("D"),
                 member.ToString("D"), revokedMember.ToString("D"), branch.ToString("D"),
                 person.ToString("D"), auditCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
             })
        start.ArgumentList.Add(value);

    using var process = Process.Start(start) ?? throw new InvalidOperationException();
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Check(process.ExitCode == 0 && output.Contains("PASS fresh-process", StringComparison.Ordinal) &&
          !error.Contains("Fictional", StringComparison.Ordinal),
        "fresh process reopened restored state without exposing record details");
}

static int VerifyRestoredProcess(string[] args)
{
    if (args.Length != 10)
        return 2;
    var databasePath = args[0];
    var key = File.ReadAllBytes(args[1]);
    var identityKey = File.ReadAllBytes(args[2]);
    try
    {
        var family = Guid.Parse(args[3]);
        var admin = Guid.Parse(args[4]);
        var member = Guid.Parse(args[5]);
        var revokedMember = Guid.Parse(args[6]);
        var branch = Guid.Parse(args[7]);
        var personId = Guid.Parse(args[8]);
        var expectedAudit = long.Parse(args[9], System.Globalization.CultureInfo.InvariantCulture);
        using (var store = new SqliteFamilyStore(databasePath, key))
        {
            Check(store.ReadPeople(family, member).Single().Id == personId,
                "restored approved person is present");
            Check(!store.IsActiveMember(family, revokedMember),
                "restored membership revocation is preserved");
            Check(store.CountAudit(family, admin) == expectedAudit,
                "restored audit entries are preserved");
        }
        using (var registry = new SqlitePrincipalRegistry(databasePath, identityKey))
            Check(registry.FindVerifiedSubject(
                "google", "https://accounts.google.com", "synthetic-subject") is not null,
                "restored registry identity remains keyed");
        var grants = new SqliteGraphGrantStore(databasePath);
        Check(grants.ReadVisiblePersonIds(family, branch, member).Contains(personId),
            "restored member visibility grant is preserved");
        Expect<UnauthorizedAccessException>(() =>
            grants.ReadVisiblePersonIds(family, branch, revokedMember),
            "revoked member cannot use the restored grant store");
        Console.WriteLine("PASS fresh-process restored-state checks");
        return 0;
    }
    catch
    {
        Console.Error.WriteLine("Fresh-process verification failed.");
        return 3;
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(identityKey);
    }
}

static void TestSymlinkRejection(
    string directory, string databasePath, byte[] key, string backupPath, string restoreDirectory)
{
    var linkPath = Path.Combine(directory, "database-link.sqlite");
    try
    {
        File.CreateSymbolicLink(linkPath, databasePath);
        Expect<IOException>(() => SqliteRecovery.CreateBackup(
            linkPath, Path.Combine(directory, "symlink-source.backup"), key),
            "symlinked database sources are rejected");

        var backupLink = Path.Combine(directory, "backup-link.backup");
        File.CreateSymbolicLink(backupLink, backupPath);
        Expect<IOException>(() => SqliteRecovery.CreateBackup(databasePath, backupLink, key),
            "symlinked backup destinations are rejected");

        var restoreLink = Path.Combine(restoreDirectory, "restore-link.sqlite");
        File.CreateSymbolicLink(restoreLink, databasePath);
        Expect<IOException>(() => SqliteRecovery.Restore(backupPath, restoreLink, key),
            "symlinked restore destinations are rejected");
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine("SKIP symlink assertions: filesystem does not permit symbolic links");
    }
    catch (PlatformNotSupportedException)
    {
        Console.WriteLine("SKIP symlink assertions: platform does not support symbolic links");
    }
}

static void TestCliIntegration(
    string databasePath, string keyPath, byte[] familyKey, Guid family, Guid member, Person person)
{
    DirectoryInfo? root = new(AppContext.BaseDirectory);
    while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "tools", "recovery")))
        root = root.Parent;
    if (root is null)
        throw new DirectoryNotFoundException("Repository root not found.");
    var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    var cliPath = Path.Combine(root.FullName, "tools", "recovery", "bin", configuration,
        "net11.0", "FamilyTree.RecoveryTool.dll");
    Check(File.Exists(cliPath), "recovery CLI project is built for process tests");

    var backupPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "cli.backup");
    var restoredDirectory = Path.Combine(Path.GetDirectoryName(databasePath)!, "cli-restored");
    Directory.CreateDirectory(restoredDirectory);
    var restoredPath = Path.Combine(restoredDirectory, "recovered.sqlite");
    var backup = RunCli(cliPath, "backup", "--database", databasePath,
        "--output", backupPath, "--key-file", keyPath);
    Check(backup.ExitCode == 0 && backup.Output.Contains("Encrypted backup created.",
        StringComparison.Ordinal), "CLI creates an encrypted backup");
    Check(!Contains(File.ReadAllBytes(backupPath), Encoding.UTF8.GetBytes(person.DisplayName)),
        "CLI backup contains no plaintext synthetic record");
    var restore = RunCli(cliPath, "restore", "--backup", backupPath,
        "--database", restoredPath, "--key-file", keyPath);
    Check(restore.ExitCode == 0 && restore.Output.Contains("Database restored.",
        StringComparison.Ordinal), "CLI restores into a new isolated path");
    using (var restored = new SqliteFamilyStore(restoredPath, familyKey))
        Check(restored.ReadPeople(family, member).Single() == person,
            "CLI-restored database reopens with application key");
    var missingArguments = RunCli(cliPath);
    Check(missingArguments.ExitCode == 2 &&
          missingArguments.Error.Contains("Usage: backup", StringComparison.Ordinal) &&
          missingArguments.Error.Contains("or: restore", StringComparison.Ordinal),
        "CLI reports both valid command forms for missing arguments");
    var invalidCommand = RunCli(cliPath, "delete", "--database", databasePath,
        "--output", backupPath, "--key-file", keyPath);
    Check(invalidCommand.ExitCode == 2 &&
          invalidCommand.Error.Contains("Usage:", StringComparison.Ordinal),
        "CLI rejects unsupported command names");

    var wrongKeyPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "wrong-cli.key");
    var wrongKey = RandomNumberGenerator.GetBytes(32);
    try
    {
        File.WriteAllBytes(wrongKeyPath, wrongKey);
        var failed = RunCli(cliPath, "restore", "--backup", backupPath,
            "--database", Path.Combine(restoredDirectory, "wrong.sqlite"),
            "--key-file", wrongKeyPath);
        Check(failed.ExitCode != 0 &&
              failed.Error.Contains("Recovery failed;", StringComparison.Ordinal) &&
              !failed.Error.Contains(person.DisplayName, StringComparison.Ordinal),
            "CLI wrong-key error is generic and does not log record values");
        var conflict = RunCli(cliPath, "backup", "--database", databasePath,
            "--output", keyPath, "--key-file", keyPath);
        Check(conflict.ExitCode != 0, "CLI rejects a key file reused as its output");
        var caseVariantConflict = RunCli(cliPath, "backup", "--database", databasePath,
            "--output", keyPath.ToUpperInvariant(), "--key-file", keyPath);
        Check(caseVariantConflict.ExitCode != 0,
            "CLI rejects a case-variant alias of its external key file");
        var unicodeKeyPath = Path.Combine(Path.GetDirectoryName(databasePath)!,
            "recovery-\u00e9.key");
        var unicodeAliasPath = Path.Combine(Path.GetDirectoryName(databasePath)!,
            "recovery-e\u0301.key");
        File.Copy(keyPath, unicodeKeyPath);
        var unicodeConflict = RunCli(cliPath, "backup", "--database", databasePath,
            "--output", unicodeAliasPath, "--key-file", unicodeKeyPath);
        Check(unicodeConflict.ExitCode != 0,
            "CLI rejects a canonically equivalent Unicode alias of its external key");
        var shortKeyPath = Path.Combine(Path.GetDirectoryName(databasePath)!, "short-cli.key");
        File.WriteAllBytes(shortKeyPath, new byte[16]);
        var shortKey = RunCli(cliPath, "backup", "--database", databasePath,
            "--output", backupPath, "--key-file", shortKeyPath);
        Check(shortKey.ExitCode != 0 &&
              shortKey.Error.Contains("Recovery failed;", StringComparison.Ordinal),
            "CLI rejects a key file with the wrong length without revealing details");
        var relativeKey = RunCli(cliPath, "backup", "--database", databasePath,
            "--output", backupPath, "--key-file", "relative.key");
        Check(relativeKey.ExitCode != 0 &&
              relativeKey.Error.Contains("Recovery failed;", StringComparison.Ordinal),
            "CLI rejects relative key paths without revealing details");
        var wrongOptions = RunCli(cliPath, "backup", "--backup", databasePath,
            "--database", backupPath, "--key-file", keyPath);
        Check(wrongOptions.ExitCode == 2 &&
              wrongOptions.Error.Contains("Usage:", StringComparison.Ordinal),
            "CLI rejects mismatched argument names");
    }
    finally
    {
        CryptographicOperations.ZeroMemory(wrongKey);
    }
}

static (int ExitCode, string Output, string Error) RunCli(string cliPath, params string[] args)
{
    var start = new ProcessStartInfo
    {
        FileName = "dotnet",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add(cliPath);
    foreach (var argument in args)
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException();
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, output, error);
}

static byte[] DecryptTestSnapshot(byte[] envelope, byte[] key)
{
    var plaintext = new byte[envelope.Length - 36];
    using var cipher = new AesGcm(key, 16);
    cipher.Decrypt(envelope.AsSpan(8, 12), envelope.AsSpan(36),
        envelope.AsSpan(20, 16), plaintext, "FTSQLBK1"u8);
    return plaintext;
}

static byte[] EncryptTestSnapshot(byte[] plaintext, byte[] key)
{
    var envelope = new byte[plaintext.Length + 36];
    "FTSQLBK1"u8.CopyTo(envelope);
    RandomNumberGenerator.Fill(envelope.AsSpan(8, 12));
    using var cipher = new AesGcm(key, 16);
    cipher.Encrypt(envelope.AsSpan(8, 12), plaintext, envelope.AsSpan(36),
        envelope.AsSpan(20, 16), "FTSQLBK1"u8);
    return envelope;
}

static int CheckRecoveryCoverage(string coveragePath)
{
    var coverage = XDocument.Load(coveragePath);
    var sourceClass = coverage.Descendants("class")
        .FirstOrDefault(element => element.Attribute("name")?.Value ==
            "FamilyTree.Persistence.SqliteRecovery" &&
            element.Attribute("filename")?.Value.Replace('\\', '/')
                .EndsWith("src/FamilyTree.Persistence/SqliteRecovery.cs",
                    StringComparison.Ordinal) == true);
    var sourceLines = (sourceClass?.Element("lines")?.Elements("line") ?? [])
        .GroupBy(element => element.Attribute("number")!.Value)
        .ToDictionary(group => group.Key, group => group.Any(element =>
            int.TryParse((string?)element.Attribute("hits"), out var hits) && hits > 0));
    if (sourceLines.Count == 0)
    {
        Console.Error.WriteLine("Recovery coverage is missing.");
        return 2;
    }

    var covered = sourceLines.Values.Count(hit => hit);
    var percent = covered * 100.0 / sourceLines.Count;
    Console.WriteLine($"SqliteRecovery line coverage: {covered}/{sourceLines.Count} ({percent:F1}%).");
    if (percent < 90)
    {
        Console.Error.WriteLine("FAIL SqliteRecovery requires at least 90% line coverage.");
        return 1;
    }

    var branchTotals = (sourceClass?.Element("lines")?.Elements("line") ?? [])
        .Where(element => string.Equals((string?)element.Attribute("branch"), "True",
            StringComparison.OrdinalIgnoreCase))
        .Select(element => ParseBranchCoverage((string?)element.Attribute("condition-coverage")))
        .ToArray();
    var coveredBranches = branchTotals.Sum(branch => branch.Covered);
    var totalBranches = branchTotals.Sum(branch => branch.Total);
    if (totalBranches == 0)
    {
        Console.Error.WriteLine("Recovery branch coverage is missing.");
        return 2;
    }

    var branchPercent = coveredBranches * 100.0 / totalBranches;
    Console.WriteLine(
        $"SqliteRecovery branch coverage: {coveredBranches}/{totalBranches} ({branchPercent:F1}%).");
    if (branchPercent < 90)
    {
        Console.Error.WriteLine("FAIL SqliteRecovery requires at least 90% branch coverage.");
        return 1;
    }
    Console.WriteLine("PASS SqliteRecovery meets its 90% line and branch coverage gates.");
    return 0;
}

static (int Covered, int Total) ParseBranchCoverage(string? coverage)
{
    var start = coverage?.LastIndexOf('(') ?? -1;
    var end = coverage?.LastIndexOf(')') ?? -1;
    if (start < 0 || end <= start)
        throw new InvalidDataException($"Recovery branch coverage metadata is invalid: {coverage ?? "<missing>"}.");
    var counts = coverage![(start + 1)..end].Split('/');
    if (counts.Length != 2 ||
        !int.TryParse(counts[0], out var covered) ||
        !int.TryParse(counts[1], out var total))
        throw new InvalidDataException($"Recovery branch coverage metadata is invalid: {coverage}.");
    return (covered, total);
}

static bool Contains(byte[] haystack, byte[] needle)
{
    return haystack.AsSpan().IndexOf(needle) >= 0;
}

static void Check(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

static void Expect<TException>(Action action, string reason) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(reason);
}
