using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;
using Permission = FamilyTree.Persistence.SqliteFamilyStore.ScopedRelationshipPermission;

internal static class SqliteRelationshipVisibilityAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var folder = Path.Join(Path.GetTempPath(), "relationship-visibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, "fictional.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid(); var branch = Guid.NewGuid(); var otherBranch = Guid.NewGuid();
        var admin = Guid.NewGuid(); var author = Guid.NewGuid(); var reviewer = Guid.NewGuid(); var viewer = Guid.NewGuid();
        var first = new Person(Guid.NewGuid(), "Fictional One");
        var second = new Person(Guid.NewGuid(), "Fictional Two");
        try
        {
            using var store = new SqliteFamilyStore(path, key);
            store.CreateFictionalFamily(family, admin);
            foreach (var member in new[] { author, reviewer, viewer }) Join(store, family, admin, member);
            foreach (var person in new[] { first, second })
            {
                var p = store.ProposePerson(family, author, person);
                store.ReviewPerson(family, admin, p, true);
            }
            var visibility = new SqliteGraphGrantStore(path);
            foreach (var person in new[] { first, second })
                foreach (var member in new[] { admin, author, reviewer, viewer })
                    GrantEndpoint(visibility, family, branch, admin, member, person.Id);
            store.GrantScopedRelationshipPermission(family, branch, admin, author, Permission.Propose);
            store.GrantScopedRelationshipPermission(family, branch, admin, reviewer, Permission.Review);
            store.GrantScopedRelationshipPermission(family, branch, admin, reviewer, Permission.Undo);
            Guid Approve()
            {
                var proposal = store.ProposeScopedRelationship(family, branch, author, first.Id, second.Id,
                    RelationKind.Friend, AssociationCategory.Social, "Fictional citation");
                store.ReviewScopedRelationship(family, branch, reviewer, proposal, true);
                return proposal;
            }
            var proposal = Approve();
            var relation = store.ListScopedRelationshipsForUndo(family, branch, reviewer).Entries.Single().Provenance!.Link.Id;
            Check(store.GetVisibleScopedRelationship(family, branch, author, relation) is null &&
                store.GetVisibleScopedRelationship(family, branch, reviewer, relation) is null &&
                store.ListVisibleScopedRelationships(family, branch, viewer).Entries.Count == 0,
                "membership, author and reviewer do not imply read visibility");
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, relation));
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, Guid.NewGuid()));
            store.PromoteAdministrator(family, admin, author);
            store.GrantScopedRelationshipPermission(family, branch, admin, author, Permission.Undo);
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(family, branch, author, viewer, relation));
            store.GrantScopedRelationshipPermission(family, branch, admin, admin, Permission.Review);
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, relation));
            store.GrantScopedRelationshipPermission(family, branch, admin, admin, Permission.Undo);
            foreach (var person in new[] { first, second })
                foreach (var member in new[] { admin, viewer })
                    GrantEndpoint(visibility, family, otherBranch, admin, member, person.Id);
            store.GrantScopedRelationshipPermission(family, otherBranch, admin, admin, Permission.Undo);
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(family, otherBranch, admin, viewer, relation));
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(family, branch, reviewer, viewer, relation));
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(Guid.NewGuid(), branch, admin, viewer, relation));
            var otherFamily = Guid.NewGuid(); var otherAdmin = Guid.NewGuid(); var otherViewer = Guid.NewGuid();
            store.CreateFictionalFamily(otherFamily, otherAdmin);
            Join(store, otherFamily, otherAdmin, otherViewer);
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(otherFamily, branch, otherAdmin, otherViewer, relation));
            store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, relation);
            store.GrantScopedRelationshipVisibility(family, branch, admin, author, relation);
            store.RevokeScopedRelationshipPermission(family, branch, admin, author, Permission.Undo);
            store.GrantScopedRelationshipVisibility(family, branch, author, reviewer, relation);
            Check(store.GetVisibleScopedRelationship(family, branch, reviewer, relation)?.Link.Id == relation,
                "an explicitly visible administrator can delegate without action permission");
            Check(store.GetVisibleScopedRelationship(family, branch, viewer, relation)?.Link.Id == relation,
                "explicit grant exposes authenticated publication");
            using (var reopened = new SqliteFamilyStore(path, key))
                Check(reopened.GetVisibleScopedRelationship(family, branch, viewer, relation)?.Citation == "Fictional citation",
                    "grant survives restart");
            Check(store.GetVisibleScopedRelationship(family, otherBranch, viewer, relation) is null &&
                store.GetVisibleScopedRelationship(Guid.NewGuid(), branch, viewer, relation) is null &&
                store.GetVisibleScopedRelationship(family, branch, viewer, Guid.NewGuid()) is null,
                "cross scope and unknown reads are indistinguishable");
            visibility.RevokeDisplayName(family, branch, admin, viewer, first.Id);
            Check(store.GetVisibleScopedRelationship(family, branch, viewer, relation) is null,
                "revoked field hides relationship");
            visibility.GrantDisplayName(family, branch, admin, viewer, first.Id);
            visibility.RevokePerson(family, branch, admin, viewer, second.Id);
            Check(store.ListVisibleScopedRelationships(family, branch, viewer).Entries.Count == 0,
                "revoked endpoint hides relationship");
            GrantEndpoint(visibility, family, branch, admin, viewer, second.Id);
            using (var db = Connect(path))
            {
                var original = Blob(db, "SELECT payload FROM scoped_relationship_publications WHERE relation_id=$id;", relation);
                Update(db, "UPDATE scoped_relationship_publications SET payload=$value WHERE relation_id=$id;", relation, [1]);
                Check(store.GetVisibleScopedRelationship(family, branch, viewer, relation) is null,
                    "modified publication fails closed");
                ExpectDenied(() => store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, relation));
                Update(db, "UPDATE scoped_relationship_publications SET payload=$value WHERE relation_id=$id;", relation, original);
                var seal = Blob(db, "SELECT seal FROM scoped_relationship_proposals WHERE id=$id;", proposal);
                Update(db, "UPDATE scoped_relationship_proposals SET seal=$value WHERE id=$id;", proposal, [1]);
                Check(store.GetVisibleScopedRelationship(family, branch, viewer, relation) is null,
                    "modified approval seal fails closed");
                Update(db, "UPDATE scoped_relationship_proposals SET seal=$value WHERE id=$id;", proposal, seal);
                var grant = Blob(db, "SELECT envelope FROM scoped_relationship_visibility WHERE relation_id=$id;", relation);
                Update(db, "UPDATE scoped_relationship_visibility SET envelope=$value WHERE relation_id=$id;", relation, [1]);
                Check(store.GetVisibleScopedRelationship(family, branch, viewer, relation) is null,
                    "modified grant fails closed");
                Update(db, "UPDATE scoped_relationship_visibility SET envelope=$value WHERE relation_id=$id;", relation, grant);
            }
            store.RevokeScopedRelationshipVisibility(family, branch, admin, viewer, relation);
            store.RevokeScopedRelationshipVisibility(family, branch, admin, viewer, Guid.NewGuid());
            Check(store.GetVisibleScopedRelationship(family, branch, viewer, relation) is null, "explicit revoke");
            store.RevokeScopedRelationshipPermission(family, branch, admin, admin, Permission.Undo);
            ExpectDenied(() => store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, relation));
            store.GrantScopedRelationshipPermission(family, branch, admin, admin, Permission.Undo);
            store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, relation);
            store.RevokeMember(family, admin, viewer);
            Join(store, family, admin, viewer);
            foreach (var person in new[] { first, second }) GrantEndpoint(visibility, family, branch, admin, viewer, person.Id);
            Check(store.GetVisibleScopedRelationship(family, branch, viewer, relation) is null,
                "rejoin does not inherit grant after regranting endpoints");
            store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, relation);
            store.UndoScopedRelationship(family, branch, reviewer, proposal);
            Check(store.GetVisibleScopedRelationship(family, branch, viewer, relation) is null, "undo revokes visibility");
            var replacement = Approve();
            var newRelation = store.ListScopedRelationshipsForUndo(family, branch, reviewer).Entries.Single().Provenance!.Link.Id;
            Check(replacement != proposal && newRelation != relation &&
                store.GetVisibleScopedRelationship(family, branch, viewer, newRelation) is null,
                "new approval requires fresh relationship grant");
            store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, newRelation);
            for (var i = 0; i < 100; i++)
            {
                var p = Approve();
                var r = RelationId(path, p);
                store.GrantScopedRelationshipVisibility(family, branch, admin, viewer, r);
            }
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    PRAGMA foreign_keys=OFF;
                    WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<140)
                    INSERT INTO scoped_relationship_visibility
                    SELECT family_id,branch_id,principal_id,printf('00000000-0000-0000-0000-%012x',n.i),envelope
                    FROM scoped_relationship_visibility,n WHERE family_id=$f AND branch_id=$b
                      AND principal_id=$p AND relation_id=$r;
                    """;
                cmd.Parameters.AddWithValue("$f", family.ToString("D"));
                cmd.Parameters.AddWithValue("$b", branch.ToString("D"));
                cmd.Parameters.AddWithValue("$p", viewer.ToString("D"));
                cmd.Parameters.AddWithValue("$r", newRelation.ToString("D"));
                Check(cmd.ExecuteNonQuery() == 140, "insert unauthenticated synthetic grants");
            }
            var page = store.ListVisibleScopedRelationships(family, branch, viewer);
            Check(page.Entries.Count == 100 && page.NextCursor is not null, "first page has continuation");
            var next = store.ListVisibleScopedRelationships(family, branch, viewer, page.NextCursor);
            Check(next.Entries.Count == 1 && next.NextCursor is null &&
                page.Entries.Select(x => x.Link.Id).Intersect(next.Entries.Select(x => x.Link.Id)).Count() == 0,
                "pagination reaches remaining distinct visible entry");
            Console.WriteLine("PASS scoped relationship visibility, authorization, integrity and pagination");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(folder, true);
        }
    }

    private static void Join(SqliteFamilyStore store, Guid family, Guid admin, Guid member)
    {
        var now = DateTimeOffset.UtcNow;
        var invitation = store.IssueInvitation(family, admin, member, now.AddDays(1), now);
        store.AcceptInvitation(family, member, invitation, now);
        store.ApproveMember(family, admin, member);
    }
    private static void GrantEndpoint(SqliteGraphGrantStore grants, Guid family, Guid branch,
        Guid admin, Guid member, Guid person)
    {
        grants.GrantPerson(family, branch, admin, member, person);
        grants.GrantDisplayName(family, branch, admin, member, person);
    }
    private static SqliteConnection Connect(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }
    private static byte[] Blob(SqliteConnection db, string sql, Guid id)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        return (byte[])cmd.ExecuteScalar()!;
    }
    private static void Update(SqliteConnection db, string sql, Guid id, byte[] value)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id.ToString("D")); cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
    private static Guid RelationId(string path, Guid proposal)
    {
        using var db = Connect(path); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT relation_id FROM scoped_relationship_proposals WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", proposal.ToString("D"));
        return Guid.Parse((string)cmd.ExecuteScalar()!);
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void ExpectDenied(Action action)
    {
        try { action(); } catch (UnauthorizedAccessException ex) when (ex.Message == "Access denied.") { return; }
        throw new Exception("Expected generic access denial.");
    }
}
