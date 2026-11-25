using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

internal static class SqliteRelationshipBoundaryAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "familytree-synthetic-relationship-boundaries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "relationships.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var administrator = Guid.NewGuid();
        var author = Guid.NewGuid();
        var first = new Person(Guid.NewGuid(), "Fictional Boundary One");
        var second = new Person(Guid.NewGuid(), "Fictional Boundary Two");
        try
        {
            using (var store = new SqliteFamilyStore(path, key))
            {
                store.CreateFictionalFamily(family, administrator);
                var invitation = store.IssueInvitation(family, administrator, author,
                    DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
                store.AcceptInvitation(family, author, invitation, DateTimeOffset.UtcNow);
                store.ApproveMember(family, administrator, author);
                store.ReviewPerson(family, administrator,
                    store.ProposePerson(family, author, first), true);
                store.ReviewPerson(family, administrator,
                    store.ProposePerson(family, author, second), true);

                Expect<ArgumentException>(() =>
                    store.ReadRelationships(Guid.Empty, administrator),
                    "empty family identifier rejected");
                Expect<ArgumentException>(() =>
                    store.ReadRelationships(family, Guid.Empty),
                    "empty viewer identifier rejected");

                var pending = Relationship.Create(first.Id, second.Id,
                    RelationKind.Friend, "synthetic-pending");
                var pendingProposal = store.ProposeRelationship(family, author, pending);
                var pendingRow = ReadProposal(path, family, pendingProposal);
                foreach (var (column, value) in new[]
                {
                    ("relation_id", "not-a-guid"),
                    ("relation_id", Guid.Empty.ToString("D")),
                    ("from_id", "not-a-guid"),
                    ("from_id", Guid.Empty.ToString("D")),
                    ("to_id", "not-a-guid"),
                    ("to_id", Guid.Empty.ToString("D")),
                    ("to_id", pendingRow.From),
                    ("kind", "not-a-kind"),
                    ("kind", "999")
                })
                {
                    Update(path, "relationship_proposals", column, value,
                        family, pendingProposal);
                    Expect<InvalidDataException>(() =>
                        store.ReviewRelationship(family, administrator,
                            pendingProposal, true),
                        $"tampered proposal {column} rejected");
                    Update(path, "relationship_proposals", column,
                        pendingRow.Value(column), family, pendingProposal);
                }

                Expect<ArgumentException>(() =>
                    store.ListPendingRelationships(family, administrator, "rp:%%%") ,
                    "malformed base64 pending cursor rejected");
                Expect<ArgumentException>(() =>
                    store.ListPendingRelationships(family, administrator, "rp://8="),
                    "invalid UTF-8 pending cursor rejected");

                var approved = Relationship.Create(first.Id, second.Id,
                    RelationKind.Spouse, "synthetic-approved");
                var approvedProposal = store.ProposeRelationship(family, author, approved);
                store.ReviewRelationship(family, administrator, approvedProposal, true);
                foreach (var (column, value) in new[]
                {
                    ("id", "not-a-guid"),
                    ("id", Guid.Empty.ToString("D")),
                    ("from_id", "not-a-guid"),
                    ("from_id", Guid.Empty.ToString("D")),
                    ("to_id", "not-a-guid"),
                    ("to_id", Guid.Empty.ToString("D")),
                    ("to_id", approved.FromPersonId.ToString("D")),
                    ("kind", "not-a-kind"),
                    ("kind", "999"),
                    ("proposal_id", Guid.Empty.ToString("D")),
                    ("submitter", Guid.Empty.ToString("D"))
                })
                {
                    UpdateRelationship(path, column, value, family, approved.Id,
                        approvedProposal);
                    Expect<InvalidDataException>(() => store.ReadRelationships(family, author),
                        $"tampered published relationship {column} rejected");
                    UpdateRelationship(path, column, RelationshipValue(column, approved,
                        approvedProposal, author), family, approved.Id, approvedProposal);
                }

                Expect<ArgumentException>(() =>
                    store.ListApprovedRelationshipsForUndo(
                        family, administrator, "rp:%%%"),
                    "malformed base64 undo cursor rejected");
                Expect<ArgumentException>(() =>
                    store.ListApprovedRelationshipsForUndo(
                        family, administrator, "rp://8="),
                    "invalid UTF-8 undo cursor rejected");
                Execute(path, """
                    DELETE FROM relationships WHERE family_id=$family AND id=$id;
                    """, family, approved.Id);
                Expect<InvalidOperationException>(() =>
                    store.UndoRelationship(family, administrator, approvedProposal),
                    "undo fails closed when the published relationship is missing");

                var removedEndpoint = Relationship.Create(first.Id, second.Id,
                    RelationKind.Neighbor, "synthetic-missing-endpoint");
                var removedProposal = store.ProposeRelationship(
                    family, author, removedEndpoint);
                Execute(path, """
                    DELETE FROM people WHERE family_id=$family AND id=$person;
                    """, family, removedEndpoint.ToPersonId);
                Expect<InvalidOperationException>(() =>
                    store.ReviewRelationship(family, administrator,
                        removedProposal, true),
                    "proposal with a removed endpoint cannot be approved");
            }
            Console.WriteLine("PASS SQLite relationship identity, endpoint, cursor and undo boundaries");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (string Relation, string From, string To, string Kind) ReadProposal(
        string path, Guid family, Guid proposal)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relation_id,from_id,to_id,kind FROM relationship_proposals
            WHERE family_id=$family AND id=$id;
            """;
        command.Parameters.AddWithValue("$family", family.ToString("D"));
        command.Parameters.AddWithValue("$id", proposal.ToString("D"));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("Synthetic proposal unavailable.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3));
    }

    private static string Value(
        this (string Relation, string From, string To, string Kind) row, string column) =>
        column switch
        {
            "relation_id" => row.Relation,
            "from_id" => row.From,
            "to_id" => row.To,
            "kind" => row.Kind,
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };

    private static string RelationshipValue(string column, Relationship relationship,
        Guid proposal, Guid submitter) =>
        column switch
        {
            "id" => relationship.Id.ToString("D"),
            "from_id" => relationship.FromPersonId.ToString("D"),
            "to_id" => relationship.ToPersonId.ToString("D"),
            "kind" => relationship.Kind.ToString(),
            "proposal_id" => proposal.ToString("D"),
            "submitter" => submitter.ToString("D"),
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };

    private static void Update(string path, string table, string column, string value,
        Guid family, Guid proposal)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE {table} SET {column}=$value WHERE family_id=$family AND id=$id;";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$family", family.ToString("D"));
        command.Parameters.AddWithValue("$id", proposal.ToString("D"));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Synthetic proposal update failed.");
    }

    private static void UpdateRelationship(string path, string column, string value,
        Guid family, Guid relationship, Guid proposal)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            $"UPDATE relationships SET {column}=$value WHERE family_id=$family AND (id=$id OR proposal_id=$proposal);";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$family", family.ToString("D"));
        command.Parameters.AddWithValue("$id", relationship.ToString("D"));
        command.Parameters.AddWithValue("$proposal", proposal.ToString("D"));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Synthetic relationship update failed.");
    }

    private static void Execute(string path, string sql, Guid family, Guid id)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$family", family.ToString("D"));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$person", id.ToString("D"));
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            ForeignKeys = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Expect<T>(Action action, string reason) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(reason);
    }
}
