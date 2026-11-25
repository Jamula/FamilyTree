using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

internal static class SqlitePrincipalRegistryAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "familytree-fictional-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Join(directory, "identity.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        const string google = "https://accounts.google.com";
        const string subject = "synthetic-subject-123";
        var microsoft = "https://login.microsoftonline.com/" +
            Guid.NewGuid().ToString("D") + "/v2.0";
        try
        {
            Expect<ArgumentException>(() => new SqlitePrincipalRegistry(
                "relative.db", key), "reject relative path");
            Expect<ArgumentException>(() => new SqlitePrincipalRegistry(
                dbPath, new byte[8]), "reject weak HMAC key");
            Expect<ArgumentNullException>(() => new SqlitePrincipalRegistry(
                dbPath, null!), "reject missing HMAC key");
            var parentIsAFile = Path.Join(directory, "not-a-directory");
            File.WriteAllText(parentIsAFile, "fictional test blocker");
            Expect<IOException>(() => new SqlitePrincipalRegistry(
                Path.Join(parentIsAFile, "unopenable.sqlite"), key),
                "constructor cleans cloned key on directory creation failure");
            Guid bound, msId, secondTenantId;
            var otherMicrosoft = "https://login.microsoftonline.com/" +
                Guid.NewGuid().ToString("D") + "/v2.0";
            using (var registry = new SqlitePrincipalRegistry(dbPath, key))
            {
                Check(registry.FindVerifiedSubject("google", google, subject) is null,
                    "unknown principal has no implicit mapping");
                bound = registry.EnrollVerifiedSubject("google", google, subject);
                Check(bound != Guid.Empty, "stored mapping minted opaque identifier");
                Check(registry.EnrollVerifiedSubject("google", google, subject) == bound,
                    "repeat enrollment idempotent and does not create another principal");
                Check(registry.FindVerifiedSubject("google", google, subject) == bound,
                    "mapping can be resolved without family grant");
                Check(registry.FindVerifiedSubject("google", google, "other-synthetic") is null,
                    "different subject cannot resolve");
                msId = registry.EnrollVerifiedSubject("microsoft", microsoft, subject);
                Check(msId != bound && msId != Guid.Empty,
                    "same subject under different issuer/provider is distinct");
                secondTenantId = registry.EnrollVerifiedSubject(
                    "microsoft", otherMicrosoft, subject);
                Check(secondTenantId != msId && secondTenantId != bound &&
                    registry.FindVerifiedSubject("microsoft", microsoft, subject) == msId &&
                    registry.FindVerifiedSubject(
                        "microsoft", otherMicrosoft, subject) == secondTenantId,
                    "two valid Microsoft tenant issuers must never share a principal");
                Expect<ArgumentException>(() => registry.EnrollVerifiedSubject(
                    "google", "http://accounts.google.com", subject), "reject HTTP issuer");
                Expect<ArgumentException>(() => registry.FindVerifiedSubject(
                    "google", "https://evil.example", subject), "reject foreign issuer");
                Expect<ArgumentException>(() => registry.FindVerifiedSubject(
                    "microsoft", "https://login.microsoftonline.com/common/v2.0",
                    subject), "reject broad tenant issuer");
                Expect<ArgumentException>(() => registry.FindVerifiedSubject(
                    "microsoft", "https://login.microsoftonline.com/" +
                    Guid.Empty.ToString("D") + "/v2.0", subject),
                    "reject empty tenant");
                foreach (var invalid in new[] { "", " ", "\n", "abc\0def",
                    " trailing", new string('A',257) })
                    Expect<ArgumentException>(() => registry.EnrollVerifiedSubject(
                        "google", google, invalid), "reject malformed subject");
                Expect<ArgumentException>(() => registry.EnrollVerifiedSubject(
                    "unknown", google, subject), "reject unknown provider");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(dbPath)).Contains(
                    subject, StringComparison.Ordinal),
                    "identity subject must never appear in SQLite plaintext");
            }
            string keyDigest;
            using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = dbPath, Pooling = false }.ToString()))
            {
                raw.Open();
                using var read = raw.CreateCommand();
                read.CommandText = "SELECT digest FROM identity_key_verifier WHERE id=1;";
                keyDigest = (string)read.ExecuteScalar()!;
                using var delete = raw.CreateCommand();
                delete.CommandText = "DELETE FROM identity_key_verifier WHERE id=1;";
                Check(delete.ExecuteNonQuery() == 1, "remove synthetic key verifier");
            }
            Expect<InvalidDataException>(() =>
                new SqlitePrincipalRegistry(dbPath, key),
                "existing identity data without key metadata fails closed");
            using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = dbPath, Pooling = false }.ToString()))
            {
                raw.Open();
                using var restore = raw.CreateCommand();
                restore.CommandText = "INSERT INTO identity_key_verifier(id,digest) VALUES(1,$digest);";
                restore.Parameters.AddWithValue("$digest", keyDigest);
                Check(restore.ExecuteNonQuery() == 1, "restore synthetic key verifier");
            }
            using (var reopened = new SqlitePrincipalRegistry(dbPath, key))
            {
                Check(reopened.FindVerifiedSubject("google", google, subject) == bound,
                    "Google identity mapping survives restart");
                Check(reopened.FindVerifiedSubject("microsoft", microsoft, subject) == msId &&
                    reopened.FindVerifiedSubject(
                        "microsoft", otherMicrosoft, subject) == secondTenantId,
                    "two valid Microsoft tenants keep distinct durable identities after restart");
            }
            Expect<InvalidDataException>(() =>
                new SqlitePrincipalRegistry(dbPath, wrongKey),
                "wrong HMAC key fails closed rather than silently splitting identities");
            using (var verifyAgain = new SqlitePrincipalRegistry(dbPath, key))
                Check(verifyAgain.FindVerifiedSubject("google", google, subject) == bound,
                    "correct key still resolves after rejected wrong-key attempt");
            using (var disposed = new SqlitePrincipalRegistry(dbPath, key))
            {
                disposed.Dispose();
                Expect<ObjectDisposedException>(() => disposed.FindVerifiedSubject(
                    "google", google, subject), "disposed key unavailable");
            }
            Console.WriteLine("PASS durable opaque OIDC identity mapping, issuer isolation, restart and denial tests");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(wrongKey);
            Directory.Delete(directory, true);
        }
    }
    private static void Check(bool ok,string message)
    {if(!ok) throw new InvalidOperationException(message);}
    private static void Expect<T>(Action action,string message) where T:Exception
    {
        var caught = false;
        try { action(); } catch(T) { caught = true; }
        if(!caught) throw new InvalidOperationException(message);
    }
}
