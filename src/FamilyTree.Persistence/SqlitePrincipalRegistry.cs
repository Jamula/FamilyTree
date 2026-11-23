using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

/// <summary>
/// A durable, opaque external identity registry. This is NOT an authentication
/// mechanism and never creates family memberships. Call its methods ONLY after
/// ASP.NET Core OIDC has verified the signed issuer, audience and subject.
/// No web endpoint currently exposes this registry.
/// </summary>
public sealed class SqlitePrincipalRegistry : IDisposable
{
    private readonly string _path;
    private readonly byte[] _key;
    private bool _disposed;

    public SqlitePrincipalRegistry(string absolutePath, byte[] hmacKey)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) ||
            !Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("Absolute database path required.", nameof(absolutePath));
        ArgumentNullException.ThrowIfNull(hmacKey);
        if (hmacKey.Length != 32)
            throw new ArgumentException("256-bit identity HMAC key required.", nameof(hmacKey));
        _path = Path.GetFullPath(absolutePath);
        _key = (byte[])hmacKey.Clone();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var db = Open();
        using var sql = db.CreateCommand();
        sql.CommandText = """
            CREATE TABLE IF NOT EXISTS external_principals (
              subject_digest TEXT NOT NULL PRIMARY KEY,
              principal_id TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS identity_key_verifier (
              id INTEGER NOT NULL PRIMARY KEY CHECK(id=1),
              digest TEXT NOT NULL
            );
            """;
        try
        {
            sql.ExecuteNonQuery();
            VerifyKey(db);
        }
        catch
        {
            // Never retain a copied key after failed store initialization.
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
            throw;
        }
    }

    private void VerifyKey(SqliteConnection db)
    {
        var expected = Convert.ToHexString(HMACSHA256.HashData(
            _key, "familytree-principal-key-v1"u8));
        using var transaction = db.BeginTransaction();
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT digest FROM identity_key_verifier WHERE id=1;";
        var stored = query.ExecuteScalar() as string;
        if (stored is null)
        {
            using var count = db.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM external_principals;";
            if (Convert.ToInt64(count.ExecuteScalar()) != 0)
                throw new InvalidDataException("Existing identities lack verified key metadata.");
            using var create = db.CreateCommand();
            create.Transaction = transaction;
            create.CommandText =
                "INSERT INTO identity_key_verifier(id,digest) VALUES(1,$digest);";
            create.Parameters.AddWithValue("$digest", expected);
            create.ExecuteNonQuery();
        }
        else if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(stored)))
        {
            throw new InvalidDataException("Identity database HMAC key does not match.");
        }
        transaction.Commit();
    }

    // A logged-in principal does not imply active FamilyMembership.
    public Guid EnrollVerifiedSubject(string provider, string issuer, string subject)
    {
        var digest = Digest(provider, issuer, subject);
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var insert = db.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO external_principals(subject_digest, principal_id)
                VALUES($digest, $principal);
                """;
            insert.Parameters.AddWithValue("$digest", digest);
            insert.Parameters.AddWithValue("$principal", Guid.NewGuid().ToString("D"));
            insert.ExecuteNonQuery();
        }
        var result = Find(db, transaction, digest);
        transaction.Commit();
        return result ?? throw new InvalidOperationException("Identity binding unavailable.");
    }

    public Guid? FindVerifiedSubject(string provider, string issuer, string subject)
    {
        var digest = Digest(provider, issuer, subject);
        using var db = Open();
        return Find(db, null, digest);
    }

    private static Guid? Find(SqliteConnection db, SqliteTransaction? transaction, string digest)
    {
        using var query = db.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT principal_id FROM external_principals
            WHERE subject_digest=$digest;
            """;
        query.Parameters.AddWithValue("$digest", digest);
        return query.ExecuteScalar() is string value ? Guid.Parse(value) : null;
    }

    private string Digest(string provider, string issuer, string subject)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (provider is not ("google" or "microsoft") ||
            string.IsNullOrWhiteSpace(subject) || subject.Length > 256 ||
            subject.Any(char.IsControl) || subject != subject.Trim())
            throw new ArgumentException("Invalid verified external subject.");
        if (provider == "google")
        {
            if (issuer != "https://accounts.google.com")
                throw new ArgumentException("Untrusted Google issuer.");
        }
        else
        {
            const string prefix = "https://login.microsoftonline.com/";
            if (issuer is null || !issuer.StartsWith(prefix, StringComparison.Ordinal) ||
                !issuer.EndsWith("/v2.0", StringComparison.Ordinal) ||
                !Guid.TryParse(issuer[prefix.Length..^"/v2.0".Length], out var tenant) ||
                tenant == Guid.Empty ||
                issuer != $"{prefix}{tenant:D}/v2.0")
                throw new ArgumentException("Untrusted Microsoft tenant issuer.");
        }
        var serialized = Encoding.UTF8.GetBytes(provider + "\0" + issuer + "\0" + subject);
        try
        {
            return Convert.ToHexString(HMACSHA256.HashData(_key, serialized));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        conn.Open();
        return conn;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }
}
