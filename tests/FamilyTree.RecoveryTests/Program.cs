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
    var directory = Path.Combine(Path.GetTempPath(), "familytree-recovery-" + Guid.NewGuid().ToString("N"));
    var restoreDirectory = Path.Combine(directory, "restored");
    Directory.CreateDirectory(directory);
    Directory.CreateDirectory(restoreDirectory);
    var databasePath = Path.Combine(directory, "synthetic.sqlite");
    var backupPath = Path.Combine(directory, "synthetic.backup");
    var restoredPath = Path.Combine(restoreDirectory, "recovered.sqlite");
    var keyPath = Path.Combine(directory, "recovery.key");
    var familyKeyPath = Path.Combine(directory, "family.key");
    var identityKeyPath = Path.Combine(directory, "identity.key");
    var familyKey = RandomNumberGenerator.GetBytes(32);
    var identityKey = RandomNumberGenerator.GetBytes(32);
    var backupKey = RandomNumberGenerator.GetBytes(32);
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
        Expect<ArgumentException>(() => SqliteRecovery.CreateBackup(
            databasePath, databasePath + "-wal", backupKey),
            "backup cannot replace the source WAL sidecar");
        Expect<ArgumentException>(() => SqliteRecovery.Restore(backupPath, backupPath, backupKey),
            "restore cannot replace its encrypted source");
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
    var sourceLines = coverage.Descendants("class")
        .Where(element => element.Attribute("filename")?.Value
            .Replace('\\', '/').EndsWith("src/FamilyTree.Persistence/SqliteRecovery.cs",
                StringComparison.Ordinal) == true)
        .SelectMany(element => element.Element("lines")?.Elements("line") ?? [])
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
    Console.WriteLine("PASS SqliteRecovery meets its 90% line-coverage gate.");
    return 0;
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
