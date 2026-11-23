using Microsoft.Data.Sqlite;

namespace FamilyTree.Persistence;

/// <summary>
/// Durable, explicit family/branch/person visibility grants. A trusted server
/// obtains the principal ID from verified OIDC and never from a request body.
/// No private HTTP routes are enabled. Membership alone grants no graph data.
/// </summary>
public sealed class SqliteGraphGrantStore
{
    private readonly string _path;

    public SqliteGraphGrantStore(string absoluteDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(absoluteDatabasePath) ||
            !Path.IsPathFullyQualified(absoluteDatabasePath))
            throw new ArgumentException("Absolute database path required.",
                nameof(absoluteDatabasePath));
        _path = Path.GetFullPath(absoluteDatabasePath);
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS person_visibility (
                family_id TEXT NOT NULL,
                branch_id TEXT NOT NULL,
                principal_id TEXT NOT NULL,
                person_id TEXT NOT NULL,
                PRIMARY KEY (family_id,branch_id,principal_id,person_id),
                FOREIGN KEY (family_id,principal_id)
                    REFERENCES memberships(family_id,principal_id),
                FOREIGN KEY (family_id,person_id)
                    REFERENCES people(family_id,id)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void GrantPerson(Guid familyId, Guid branchId, Guid adminId,
        Guid recipientId, Guid personId)
    {
        RequireIds(familyId, branchId, adminId, recipientId, personId);
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (!IsActive(db,tx,familyId,adminId,true) ||
            !IsActive(db,tx,familyId,recipientId,false))
            throw new UnauthorizedAccessException("Access denied.");
        using var cmd = db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO person_visibility
              (family_id,branch_id,principal_id,person_id)
            SELECT $family,$branch,$recipient,id FROM people
            WHERE family_id=$family AND id=$person;
            """;
        cmd.Parameters.AddWithValue("$family", familyId.ToString("D"));
        cmd.Parameters.AddWithValue("$branch", branchId.ToString("D"));
        cmd.Parameters.AddWithValue("$recipient", recipientId.ToString("D"));
        cmd.Parameters.AddWithValue("$person", personId.ToString("D"));
        if (cmd.ExecuteNonQuery()==0 && !IsVisible(db,tx,familyId,branchId,recipientId,personId))
            throw new UnauthorizedAccessException("Access denied.");
        tx.Commit();
    }

    public void RevokePerson(Guid familyId, Guid branchId, Guid adminId,
        Guid recipientId, Guid personId)
    {
        RequireIds(familyId, branchId, adminId, recipientId, personId);
        using var db=Open();
        using var tx=db.BeginTransaction();
        if (!IsActive(db,tx,familyId,adminId,true))
            throw new UnauthorizedAccessException("Access denied.");
        using var cmd=Command(db,tx, """
            DELETE FROM person_visibility WHERE family_id=$family
            AND branch_id=$branch AND principal_id=$recipient AND person_id=$person;
            """,familyId,branchId,recipientId,personId);
        cmd.ExecuteNonQuery(); // Idempotent, without revealing existence.
        tx.Commit();
    }

    public IReadOnlySet<Guid> ReadVisiblePersonIds(Guid familyId, Guid branchId,
        Guid authenticatedPrincipalId)
    {
        RequireIds(familyId,branchId,authenticatedPrincipalId);
        using var db=Open();
        using var tx=db.BeginTransaction();
        if (!IsActive(db,tx,familyId,authenticatedPrincipalId,false))
            throw new UnauthorizedAccessException("Access denied.");
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText = """
            SELECT p.person_id FROM person_visibility p
            JOIN people living ON living.family_id=p.family_id
                AND living.id=p.person_id
            WHERE p.family_id=$family AND p.branch_id=$branch
            AND p.principal_id=$principal ORDER BY p.person_id;
            """;
        cmd.Parameters.AddWithValue("$family",familyId.ToString("D"));
        cmd.Parameters.AddWithValue("$branch",branchId.ToString("D"));
        cmd.Parameters.AddWithValue("$principal",authenticatedPrincipalId.ToString("D"));
        var result=new HashSet<Guid>();
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())
                result.Add(Guid.Parse(reader.GetString(0)));
        tx.Commit();
        return result;
    }

    private static bool IsVisible(SqliteConnection db,SqliteTransaction tx,
        Guid family,Guid branch,Guid recipient,Guid person)
    {
        using var cmd=Command(db,tx, """
            SELECT 1 FROM person_visibility WHERE family_id=$family
            AND branch_id=$branch AND principal_id=$recipient AND person_id=$person;
            """,family,branch,recipient,person);
        return cmd.ExecuteScalar() is not null;
    }

    private static SqliteCommand Command(SqliteConnection db,SqliteTransaction tx,
        string sql,Guid family,Guid branch,Guid recipient,Guid person)
    {
        var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText=sql;
        cmd.Parameters.AddWithValue("$family",family.ToString("D"));
        cmd.Parameters.AddWithValue("$branch",branch.ToString("D"));
        cmd.Parameters.AddWithValue("$recipient",recipient.ToString("D"));
        cmd.Parameters.AddWithValue("$person",person.ToString("D"));
        return cmd;
    }

    private static bool IsActive(SqliteConnection db,SqliteTransaction tx,
        Guid family,Guid principal,bool administrator)
    {
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText = """
            SELECT 1 FROM memberships WHERE family_id=$family
            AND principal_id=$principal AND state='Active'
            AND ($admin=0 OR administrator=1);
            """;
        cmd.Parameters.AddWithValue("$family",family.ToString("D"));
        cmd.Parameters.AddWithValue("$principal",principal.ToString("D"));
        cmd.Parameters.AddWithValue("$admin",administrator ? 1 : 0);
        return cmd.ExecuteScalar() is not null;
    }

    private static void RequireIds(params Guid[] ids)
    {
        if(ids.Any(x=>x==Guid.Empty))
            throw new ArgumentException("Identifiers required.");
    }

    private SqliteConnection Open()
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource=_path,Mode=SqliteOpenMode.ReadWriteCreate,
            ForeignKeys=true,Pooling=false,DefaultTimeout=5
        }.ToString());
        db.Open();
        return db;
    }
}
