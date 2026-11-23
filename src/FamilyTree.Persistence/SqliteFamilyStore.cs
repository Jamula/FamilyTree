using System.Security.Cryptography;
using System.Text;
using FamilyTree.Domain;
using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

/// <summary>
/// A local, encrypted-name SQLite persistence slice used to exercise durable
/// family/member/person review. The caller's principal MUST come from verified
/// OIDC middleware, never a request field. No HTTP route consumes this yet.
/// This is not a production data store until key lifecycle, backup and
/// deployment access controls are reviewed. Tests use fictional names only.
/// </summary>
public sealed class SqliteFamilyStore : IDisposable
{
    private readonly string _path;
    private readonly byte[] _key;
    private bool _disposed;

    public SqliteFamilyStore(string absolutePath, byte[] encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("Absolute database path required.", nameof(absolutePath));
        ArgumentNullException.ThrowIfNull(encryptionKey);
        if (encryptionKey.Length != 32)
            throw new ArgumentException("A 256-bit encryption key is required.", nameof(encryptionKey));
        _path = Path.GetFullPath(absolutePath);
        _key = (byte[])encryptionKey.Clone();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS families (
                id TEXT PRIMARY KEY NOT NULL
            );
            CREATE TABLE IF NOT EXISTS memberships (
                family_id TEXT NOT NULL, principal_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('Pending', 'Active', 'Left')),
                administrator INTEGER NOT NULL CHECK (administrator IN (0,1)),
                PRIMARY KEY(family_id, principal_id),
                FOREIGN KEY(family_id) REFERENCES families(id)
            );
            CREATE TABLE IF NOT EXISTS invitations (
                id TEXT PRIMARY KEY, family_id TEXT NOT NULL, issuer TEXT NOT NULL,
                intended TEXT NOT NULL, expires_unix INTEGER NOT NULL,
                state TEXT NOT NULL CHECK (state IN ('Offered','Accepted')),
                FOREIGN KEY(family_id) REFERENCES families(id)
            );
            CREATE TABLE IF NOT EXISTS people (
                family_id TEXT NOT NULL, id TEXT NOT NULL,
                encrypted_name BLOB NOT NULL, living INTEGER NOT NULL,
                PRIMARY KEY(family_id, id), FOREIGN KEY(family_id) REFERENCES families(id)
            );
            CREATE TABLE IF NOT EXISTS person_proposals (
                id TEXT PRIMARY KEY, family_id TEXT NOT NULL, person_id TEXT NOT NULL,
                submitter TEXT NOT NULL, encrypted_name BLOB NOT NULL,
                living INTEGER NOT NULL, state TEXT NOT NULL
                    CHECK(state IN ('Pending','Approved','Rejected')),
                reviewer TEXT NULL, FOREIGN KEY(family_id) REFERENCES families(id)
            );
            CREATE TABLE IF NOT EXISTS audit (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                family_id TEXT NOT NULL, subject_id TEXT NOT NULL,
                reviewer TEXT NOT NULL, action TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS key_verifier (
                id INTEGER PRIMARY KEY CHECK(id=1),
                envelope BLOB NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        VerifyDatabaseKey(connection);
    }

    // Bind an on-disk family database to the verified key before any write.
    // Populated legacy DBs without a marker require explicit migration.
    private void VerifyDatabaseKey(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        byte[]? envelope;
        using (var command = Cmd(connection, tx,
            "SELECT envelope FROM key_verifier WHERE id=1;"))
            envelope = command.ExecuteScalar() as byte[];
        const string expected = "familytree-sqlite-db-key-v1";
        if (envelope is null)
        {
            if (Scalar<long>(connection, tx, "SELECT COUNT(*) FROM families;") != 0)
                throw new InvalidDataException("Unverified existing database key.");
            Exec(connection, tx, "INSERT INTO key_verifier(id,envelope) VALUES(1,$value);",
                ("$value", Encrypt(Guid.Empty, Guid.Empty, expected)));
        }
        else if (!string.Equals(
            Decrypt(Guid.Empty, Guid.Empty, envelope), expected,
            StringComparison.Ordinal))
            throw new InvalidDataException("Invalid database encryption key.");
        tx.Commit();
    }

    /// <summary>
    /// Bootstrap for isolated local development only. A hosted implementation
    /// must replace this with trusted provisioning after verified sign-in.
    /// </summary>
    public void CreateFictionalFamily(Guid familyId, Guid administratorId)
    {
        RequireIds(familyId, administratorId);
        using var con = Open();
        using var transaction = con.BeginTransaction();
        Exec(con, transaction, "INSERT INTO families(id) VALUES ($f);", ("$f", familyId.ToString("D")));
        Exec(con, transaction, """
            INSERT INTO memberships(family_id,principal_id,state,administrator)
            VALUES($f,$p,'Active',1);
            """, ("$f", familyId.ToString("D")), ("$p", administratorId.ToString("D")));
        transaction.Commit();
    }

    public Guid IssueInvitation(Guid familyId, Guid administratorId,
        Guid intendedPrincipalId, DateTimeOffset expiry, DateTimeOffset now)
    {
        RequireIds(familyId, administratorId, intendedPrincipalId);
        if (administratorId == intendedPrincipalId || expiry <= now)
            throw new ArgumentException("Invalid intended recipient or expiry.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        var id = Guid.NewGuid();
        Exec(con, tx, """
            INSERT INTO invitations(id,family_id,issuer,intended,expires_unix,state)
            VALUES($id,$f,$issuer,$intended,$expires,'Offered');
            """, ("$id", id.ToString("D")), ("$f", familyId.ToString("D")),
            ("$issuer", administratorId.ToString("D")),
            ("$intended", intendedPrincipalId.ToString("D")),
            ("$expires", expiry.ToUnixTimeSeconds()));
        tx.Commit();
        return id;
    }

    public void AcceptInvitation(Guid familyId, Guid inviteeId, Guid invitationId,
        DateTimeOffset now)
    {
        RequireIds(familyId, inviteeId, invitationId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        var updated = Exec(con, tx, """
            UPDATE invitations SET state='Accepted'
            WHERE id=$id AND family_id=$f AND intended=$p AND state='Offered'
              AND expires_unix > $now;
            """, ("$id", invitationId.ToString("D")), ("$f", familyId.ToString("D")),
            ("$p", inviteeId.ToString("D")), ("$now", now.ToUnixTimeSeconds()));
        if (updated != 1)
            throw new UnauthorizedAccessException("Invitation cannot be accepted.");
        var membership = Exec(con, tx, """
            INSERT INTO memberships(family_id,principal_id,state,administrator)
            VALUES($f,$p,'Pending',0)
            ON CONFLICT(family_id,principal_id) DO UPDATE SET state='Pending',
            administrator=0 WHERE memberships.state='Left';
            """, ("$f", familyId.ToString("D")), ("$p", inviteeId.ToString("D")));
        if (membership != 1)
            throw new UnauthorizedAccessException("Membership already exists.");
        tx.Commit();
    }

    public void ApproveMember(Guid familyId, Guid administratorId, Guid memberId)
    {
        RequireIds(familyId, administratorId, memberId);
        if (administratorId == memberId)
            throw new UnauthorizedAccessException("Self-approval denied.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        if (Exec(con, tx, """
            UPDATE memberships SET state='Active' WHERE family_id=$f
            AND principal_id=$p AND state='Pending' AND administrator=0;
            """, ("$f", familyId.ToString("D")), ("$p", memberId.ToString("D"))) != 1)
            throw new UnauthorizedAccessException("Pending membership unavailable.");
        Audit(con, tx, familyId, memberId, administratorId, "member-approved");
        tx.Commit();
    }

    /// <summary>Only an active administrator may promote another active member.</summary>
    public void PromoteAdministrator(Guid familyId, Guid administratorId,
        Guid newAdministratorId)
    {
        RequireIds(familyId, administratorId, newAdministratorId);
        if (administratorId == newAdministratorId)
            throw new UnauthorizedAccessException("Self-promotion denied.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        if (Exec(con, tx, """
            UPDATE memberships SET administrator=1
            WHERE family_id=$f AND principal_id=$p AND state='Active'
              AND administrator=0;
            """, ("$f", familyId.ToString("D")),
            ("$p", newAdministratorId.ToString("D"))) != 1)
            throw new UnauthorizedAccessException("Active member unavailable.");
        Audit(con, tx, familyId, newAdministratorId, administratorId,
            "administrator-promoted");
        tx.Commit();
    }

    public void RevokeMember(Guid familyId, Guid administratorId, Guid memberId)
    {
        RequireIds(familyId, administratorId, memberId);
        if (administratorId == memberId)
            throw new UnauthorizedAccessException("Administrator cannot revoke self.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        if (Exec(con, tx, """
            UPDATE memberships SET state='Left', administrator=0 WHERE family_id=$f
            AND principal_id=$p AND state IN ('Active','Pending');
            """, ("$f", familyId.ToString("D")), ("$p", memberId.ToString("D"))) != 1)
            throw new UnauthorizedAccessException("Membership unavailable.");
        Audit(con, tx, familyId, memberId, administratorId, "member-revoked");
        tx.Commit();
    }

    public Guid ProposePerson(Guid familyId, Guid principalId, Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        RequireIds(familyId, principalId, person.Id);
        if (string.IsNullOrWhiteSpace(person.DisplayName) ||
            person.DisplayName.Length > 200 || person.DisplayName.IndexOfAny(['\r','\n']) >= 0)
            throw new ArgumentException("Invalid person display name.");
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, principalId, administrator: false);
        var duplicates = Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM people WHERE family_id=$f AND id=$p;
            """, ("$f", familyId.ToString("D")), ("$p", person.Id.ToString("D")));
        if (duplicates != 0)
            throw new InvalidOperationException("Duplicate person ID.");
        var pendingDuplicate = Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM person_proposals
            WHERE family_id=$f AND person_id=$p AND state='Pending';
            """, ("$f", familyId.ToString("D")), ("$p", person.Id.ToString("D")));
        if (pendingDuplicate != 0)
            throw new InvalidOperationException("Duplicate pending person ID.");
        var id = Guid.NewGuid();
        var sealedName = Encrypt(familyId, person.Id, person.DisplayName);
        Exec(con, tx, """
            INSERT INTO person_proposals
            (id,family_id,person_id,submitter,encrypted_name,living,state)
            VALUES($id,$f,$p,$submitter,$name,$living,'Pending');
            """, ("$id", id.ToString("D")), ("$f", familyId.ToString("D")),
            ("$p", person.Id.ToString("D")), ("$submitter", principalId.ToString("D")),
            ("$name", sealedName), ("$living", person.IsLiving ? 1 : 0));
        tx.Commit();
        return id;
    }

    public void ReviewPerson(Guid familyId, Guid moderatorId, Guid proposalId, bool approve)
    {
        RequireIds(familyId, moderatorId, proposalId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, moderatorId, administrator: true);
        Guid personId;
        byte[] sealedName;
        long living;
        using (var q = Cmd(con, tx, """
            SELECT person_id,submitter,encrypted_name,living
            FROM person_proposals WHERE family_id=$f AND id=$id AND state='Pending';
            """, ("$f", familyId.ToString("D")), ("$id", proposalId.ToString("D"))))
        using (var reader = q.ExecuteReader())
        {
            if (!reader.Read())
                throw new InvalidOperationException("Pending proposal unavailable.");
            if (reader.GetString(1) == moderatorId.ToString("D"))
                throw new UnauthorizedAccessException("Self-review denied.");
            personId = Guid.Parse(reader.GetString(0));
            sealedName = reader.GetFieldValue<byte[]>(2);
            living = reader.GetInt64(3);
        }
        if (approve)
        {
            // The review boundary must authenticate ciphertext before it can
            // enter the approved graph. A wrong-key or tampered proposal stays pending.
            _ = Decrypt(familyId, personId, sealedName);
            Exec(con, tx, """
                INSERT INTO people(family_id,id,encrypted_name,living)
                VALUES($f,$p,$name,$living);
                """, ("$f", familyId.ToString("D")), ("$p", personId.ToString("D")),
                ("$name", sealedName), ("$living", living));
        }
        if (Exec(con, tx, """
            UPDATE person_proposals SET state=$stage,reviewer=$reviewer
            WHERE id=$id AND family_id=$f AND state='Pending';
            """, ("$stage", approve ? "Approved" : "Rejected"),
            ("$reviewer", moderatorId.ToString("D")), ("$id", proposalId.ToString("D")),
            ("$f", familyId.ToString("D"))) != 1)
            throw new InvalidOperationException("Pending proposal unavailable.");
        Audit(con, tx, familyId, proposalId, moderatorId,
            approve ? "person-approved" : "person-rejected");
        tx.Commit();
    }

    /// <summary>
    /// Administrator-only pending proposal inbox. Only a trusted OIDC-bound
    /// active administrator may invoke this; never bind to anonymous routes.
    /// No names or original content go into audit/telemetry.
    /// </summary>
    public sealed record PendingPersonReview(Guid ProposalId, Person Person, Guid SubmittedBy);

    public IReadOnlyList<PendingPersonReview> ListPendingPeople(Guid familyId, Guid administratorId)
    {
        RequireIds(familyId, administratorId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        var pending = new List<PendingPersonReview>();
        using (var q = Cmd(con, tx, """
            SELECT id,person_id,submitter,encrypted_name,living
            FROM person_proposals WHERE family_id=$f AND state='Pending' ORDER BY id
            LIMIT 100;
            """, ("$f", familyId.ToString("D"))))
        using (var reader = q.ExecuteReader())
            while (reader.Read())
            {
                var personId = Guid.Parse(reader.GetString(1));
                pending.Add(new PendingPersonReview(
                    Guid.Parse(reader.GetString(0)),
                    new Person(personId,
                        Decrypt(familyId, personId, reader.GetFieldValue<byte[]>(3)),
                        reader.GetInt64(4) != 0),
                    Guid.Parse(reader.GetString(2))));
            }
        tx.Commit();
        return pending;
    }

    public IReadOnlyList<Person> ReadPeople(Guid familyId, Guid viewerId)
    {
        RequireIds(familyId, viewerId);
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, viewerId, administrator: false);
        var people = new List<Person>();
        using (var q = Cmd(con, tx, """
            SELECT id, encrypted_name, living FROM people
            WHERE family_id=$f ORDER BY id;
            """, ("$f", familyId.ToString("D"))))
        using (var reader = q.ExecuteReader())
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                var name = Decrypt(familyId, id, reader.GetFieldValue<byte[]>(1));
                people.Add(new Person(id, name, reader.GetInt64(2) != 0));
            }
        tx.Commit();
        return people;
    }

    public long CountAudit(Guid familyId, Guid administratorId)
    {
        using var con = Open();
        using var tx = con.BeginTransaction();
        RequireMember(con, tx, familyId, administratorId, administrator: true);
        var count = Scalar<long>(con, tx,
            "SELECT COUNT(*) FROM audit WHERE family_id=$f;",
            ("$f", familyId.ToString("D")));
        tx.Commit();
        return count;
    }

    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true, Pooling = false, DefaultTimeout = 5
        }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteCommand Cmd(SqliteConnection con, SqliteTransaction tx,
        string sql, params (string Name, object Value)[] parameters)
    {
        var q = con.CreateCommand();
        q.Transaction = tx;
        q.CommandText = sql;
        foreach (var (name, value) in parameters)
            q.Parameters.AddWithValue(name, value);
        return q;
    }

    private static int Exec(SqliteConnection con, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var q = Cmd(con, tx, sql, parameters);
        return q.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection con, SqliteTransaction tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        using var q = Cmd(con, tx, sql, parameters);
        return (T)q.ExecuteScalar()!;
    }

    private static void RequireIds(params Guid[] ids)
    {
        if (ids.Any(id => id == Guid.Empty))
            throw new ArgumentException("Identifiers must be nonempty.");
    }

    private static void RequireMember(SqliteConnection con, SqliteTransaction tx,
        Guid familyId, Guid principalId, bool administrator)
    {
        RequireIds(familyId, principalId);
        var exists = Scalar<long>(con, tx, """
            SELECT COUNT(*) FROM memberships
            WHERE family_id=$f AND principal_id=$p AND state='Active'
            AND ($admin=0 OR administrator=1);
            """, ("$f", familyId.ToString("D")), ("$p", principalId.ToString("D")),
            ("$admin", administrator ? 1 : 0));
        if (exists != 1) throw new UnauthorizedAccessException("Access denied.");
    }

    private static void Audit(SqliteConnection con, SqliteTransaction tx,
        Guid family, Guid subject, Guid actor, string action)
        => Exec(con, tx, """
            INSERT INTO audit(family_id,subject_id,reviewer,action)
            VALUES($f,$subject,$actor,$action);
            """, ("$f", family.ToString("D")), ("$subject", subject.ToString("D")),
            ("$actor", actor.ToString("D")), ("$action", action));

    private byte[] Encrypt(Guid familyId, Guid personId, string name)
    {
        var clear = Encoding.UTF8.GetBytes(name);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[clear.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(nonce, clear, cipher, tag, AssociatedData(familyId, personId));
            return [..nonce, ..tag, ..cipher];
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private string Decrypt(Guid familyId, Guid personId, byte[] envelope)
    {
        if (envelope.Length < 28)
            throw new InvalidDataException("Encrypted person record invalid.");
        var clear = new byte[envelope.Length - 28];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(envelope.AsSpan(0, 12), envelope.AsSpan(28),
                envelope.AsSpan(12, 16), clear, AssociatedData(familyId, personId));
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Encrypted person record invalid.");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static byte[] AssociatedData(Guid family, Guid person)
        => [..family.ToByteArray(), ..person.ToByteArray()];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }
}
