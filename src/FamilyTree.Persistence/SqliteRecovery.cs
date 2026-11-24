using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

public static class SqliteRecovery
{
    private static readonly byte[] Magic = "FTSQLBK1"u8.ToArray();
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 8 + NonceLength + TagLength;
    private const int MaximumSnapshotLength = 1024 * 1024 * 1024;

    public static void CreateBackup(string databasePath, string backupPath, byte[] backupKey)
    {
        ValidateKey(backupKey);
        var sourcePath = ResolvePath(databasePath, nameof(databasePath));
        var targetPath = ResolvePath(backupPath, nameof(backupPath));
        RequireDistinct(sourcePath, targetPath);
        RequireNotSidecar(sourcePath, targetPath);
        RequireExistingFile(sourcePath);
        RequireExistingDirectory(Path.GetDirectoryName(targetPath)!);
        RejectDirectory(targetPath);

        using var source = Open(sourcePath, SqliteOpenMode.ReadOnly);
        using var snapshot = OpenMemory();
        source.BackupDatabase(snapshot);
        ValidateDatabase(snapshot);

        var database = Serialize(snapshot);
        try
        {
            var encrypted = Encrypt(database, backupKey);
            try
            {
                WriteAtomically(targetPath, encrypted, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(database);
        }
    }

    public static void Restore(string backupPath, string databasePath, byte[] backupKey)
    {
        ValidateKey(backupKey);
        var sourcePath = ResolvePath(backupPath, nameof(backupPath));
        var targetPath = ResolvePath(databasePath, nameof(databasePath));
        RequireDistinct(sourcePath, targetPath);
        RequireExistingFile(sourcePath);
        RequireExistingDirectory(Path.GetDirectoryName(targetPath)!);
        RejectDirectory(targetPath);
        if (File.Exists(targetPath) || HasSidecar(targetPath))
            throw new IOException("Restore destination must be new and isolated.");

        var encrypted = File.ReadAllBytes(sourcePath);
        var database = Decrypt(encrypted, backupKey);
        CryptographicOperations.ZeroMemory(encrypted);
        try
        {
            RestoreDatabase(database, targetPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(database);
        }
    }

    private static void RestoreDatabase(byte[] database, string targetPath)
    {
        var stagedPath = Path.Combine(Path.GetDirectoryName(targetPath)!,
            ".ft-recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            CreateEmptyFile(stagedPath);
            using (var stream = new FileStream(stagedPath, FileMode.Truncate, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(database);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                using var restored = Open(stagedPath, SqliteOpenMode.ReadWrite);
                NormalizeJournalMode(restored);
                ValidateDatabase(restored);
            }
            catch (SqliteException)
            {
                throw new InvalidDataException("SQLite snapshot could not be validated.");
            }
            FlushFile(stagedPath);
            if (File.Exists(targetPath) || HasSidecar(targetPath))
                throw new IOException("Restore destination must be new and isolated.");
            File.Move(stagedPath, targetPath);
        }
        finally
        {
            DeleteIfExists(stagedPath);
            DeleteIfExists(stagedPath + "-wal");
            DeleteIfExists(stagedPath + "-shm");
            DeleteIfExists(stagedPath + "-journal");
        }
    }

    private static void NormalizeJournalMode(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        if (!string.Equals(command.ExecuteScalar() as string, "delete",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite snapshot journal mode is invalid.");
    }

    private static byte[] Serialize(SqliteConnection connection)
    {
        var pointer = SQLitePCL.raw.sqlite3_serialize(connection.Handle, "main", out var length, 0);
        if (pointer == IntPtr.Zero || length <= 0 || length > MaximumSnapshotLength)
        {
            if (pointer != IntPtr.Zero)
                SQLitePCL.raw.sqlite3_free(pointer);
            throw new InvalidDataException("SQLite snapshot could not be serialized.");
        }

        try
        {
            var result = new byte[checked((int)length)];
            Marshal.Copy(pointer, result, 0, result.Length);
            return result;
        }
        finally
        {
            SQLitePCL.raw.sqlite3_free(pointer);
        }
    }

    private static byte[] Encrypt(byte[] database, byte[] key)
    {
        if (database.Length == 0 || database.Length > MaximumSnapshotLength)
            throw new InvalidDataException("SQLite snapshot size is invalid.");

        var envelope = new byte[checked(HeaderLength + database.Length)];
        Magic.CopyTo(envelope, 0);
        var nonce = envelope.AsSpan(Magic.Length, NonceLength);
        var tag = envelope.AsSpan(Magic.Length + NonceLength, TagLength);
        var ciphertext = envelope.AsSpan(HeaderLength);
        RandomNumberGenerator.Fill(nonce);
        using var cipher = new AesGcm(key, TagLength);
        cipher.Encrypt(nonce, database, ciphertext, tag, Magic);
        return envelope;
    }

    private static byte[] Decrypt(byte[] envelope, byte[] key)
    {
        if (envelope.Length <= HeaderLength ||
            envelope.Length > HeaderLength + MaximumSnapshotLength ||
            !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("Encrypted backup format is invalid.");

        var database = new byte[envelope.Length - HeaderLength];
        try
        {
            using var cipher = new AesGcm(key, TagLength);
            cipher.Decrypt(
                envelope.AsSpan(Magic.Length, NonceLength),
                envelope.AsSpan(HeaderLength),
                envelope.AsSpan(Magic.Length + NonceLength, TagLength),
                database,
                Magic);
            return database;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(database);
            throw new InvalidDataException("Encrypted backup authentication failed.");
        }
    }

    private static void ValidateDatabase(SqliteConnection connection)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check;";
            using var reader = check.ExecuteReader();
            if (!reader.Read() || !string.Equals(reader.GetString(0), "ok",
                    StringComparison.OrdinalIgnoreCase) || reader.Read())
                throw new InvalidDataException("SQLite snapshot integrity check failed.");
        }

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check;";
            using var reader = check.ExecuteReader();
            if (reader.Read())
                throw new InvalidDataException("SQLite snapshot relationship check failed.");
        }

        using (var check = connection.CreateCommand())
        {
            check.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type='table' AND name IN
                    ('families','memberships','invitations','people',
                     'person_proposals','audit','key_verifier');
                """;
            if (Convert.ToInt32(check.ExecuteScalar()) != 7)
                throw new InvalidDataException("SQLite snapshot schema is incomplete.");
        }
    }

    private static SqliteConnection OpenMemory()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 30
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void WriteAtomically(string targetPath, byte[] content, bool overwrite)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(targetPath)!,
            ".ft-recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = NewFile(temporaryPath))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (overwrite)
                File.Move(temporaryPath, targetPath, overwrite: true);
            else
                File.Move(temporaryPath, targetPath);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static FileStream NewFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static void CreateEmptyFile(string path)
    {
        using var stream = NewFile(path);
        stream.Flush(flushToDisk: true);
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new ArgumentException("A 256-bit backup key is required.", nameof(key));
    }

    private static string ResolvePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute path is required.", parameterName);
        var fullPath = Path.GetFullPath(path);
        RejectReparsePoints(fullPath);
        return fullPath;
    }

    private static void RejectReparsePoints(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Recovery paths cannot contain symbolic links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void RequireExistingFile(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("Recovery input file is unavailable.");
    }

    private static void RequireExistingDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException("Recovery directory is unavailable.");
    }

    private static void RejectDirectory(string path)
    {
        if (Directory.Exists(path))
            throw new IOException("Recovery file path is a directory.");
    }

    private static void RequireDistinct(string first, string second)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(first, second, comparison))
            throw new ArgumentException("Recovery input and output paths must differ.");
    }

    private static void RequireNotSidecar(string databasePath, string outputPath)
    {
        if (new[] { "-wal", "-shm", "-journal" }.Any(suffix =>
                PathsEqual(databasePath + suffix, outputPath)))
            throw new ArgumentException("Backup output cannot replace a SQLite sidecar.");
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(first, second, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static bool HasSidecar(string path)
    {
        var sidecars = new[] { path + "-wal", path + "-shm", path + "-journal" };
        foreach (var sidecar in sidecars)
            RejectReparsePoints(sidecar);
        return sidecars.Any(File.Exists);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException) { }
    }
}
