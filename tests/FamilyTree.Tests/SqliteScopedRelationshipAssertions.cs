using System.Security.Cryptography;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;
using Permission = FamilyTree.Persistence.SqliteFamilyStore.ScopedRelationshipPermission;
using PersonPermission = FamilyTree.Persistence.SqliteFamilyStore.ScopedPersonPermission;

internal static class SqliteScopedRelationshipAssertions
{
    internal static void Run()
    {
        var directory = Path.Join(Path.GetTempPath(), "scoped-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "synthetic.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid(); var branch = Guid.NewGuid(); var other = Guid.NewGuid();
        var admin = Guid.NewGuid(); var author = Guid.NewGuid(); var reviewer = Guid.NewGuid(); var undoer = Guid.NewGuid();
        try
        {
            using var store = new SqliteFamilyStore(path, key);
            store.CreateFictionalFamily(family, admin);
            Join(store, family, admin, author);
            Join(store, family, admin, reviewer);
            Join(store, family, admin, undoer);
            var visibility = new SqliteGraphGrantStore(path);
            var first = new Person(Guid.NewGuid(), "Synthetic First");
            var second = new Person(Guid.NewGuid(), "Synthetic Second");
            foreach (var person in new[] { first, second })
            {
                var p = store.ProposePerson(family, author, person);
                store.ReviewPerson(family, admin, p, true);
                foreach (var member in new[] { author, reviewer, undoer })
                {
                    visibility.GrantPerson(family, branch, admin, member, person.Id);
                    visibility.GrantDisplayName(family, branch, admin, member, person.Id);
                }
            }
            var legacyEdge = Relationship.Create(first.Id, second.Id, RelationKind.Friend);
            var legacyApproved = store.ProposeRelationship(family, author, legacyEdge);
            store.ReviewRelationship(family, admin, legacyApproved, true);
            var legacyPending = store.ProposeRelationship(family, author,
                Relationship.Create(first.Id, second.Id, RelationKind.Neighbor));
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO scoped_relationship_grants VALUES($family,$branch,$principal,'Propose');
                    """;
                cmd.Parameters.AddWithValue("$family", family.ToString("D"));
                cmd.Parameters.AddWithValue("$branch", branch.ToString("D"));
                cmd.Parameters.AddWithValue("$principal", author.ToString("D"));
                Check(cmd.ExecuteNonQuery() == 1, "simulate orphaned legacy scoped grant");
            }
            Expect<InvalidDataException>(() => { using var ignored = new SqliteFamilyStore(path, key); });
            Check(store.ReadRelationships(family, author).Single() == legacyEdge,
                "orphaned grant does not silently activate scoped mode");
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM scoped_relationship_grants WHERE family_id=$family;";
                cmd.Parameters.AddWithValue("$family", family.ToString("D"));
                Check(cmd.ExecuteNonQuery() == 1, "remove orphaned synthetic grant");
            }
            Guid Propose() => store.ProposeScopedRelationship(family, branch, author, first.Id,
                second.Id, RelationKind.Friend, AssociationCategory.Social, "Synthetic Citation",
                start: new DateOnly(2001, 1, 1), end: new DateOnly(2002, 1, 1));
            Expect<UnauthorizedAccessException>(() => Propose());
            var beforeActivation = AuditCount(path);
            Expect<InvalidOperationException>(() => store.GrantScopedRelationshipPermission(family, branch, admin, author, Permission.Propose));
            Check(AuditCount(path) == beforeActivation && store.ReadRelationships(family, author).Single() == legacyEdge &&
                store.ListPendingRelationships(family, admin).Any(x => x.ProposalId == legacyPending),
                "failed activation leaves legacy graph and inbox unchanged");
            store.ReviewRelationship(family, admin, legacyPending, false);
            Expect<InvalidOperationException>(() => store.GrantScopedRelationshipPermission(family, branch, admin, author, Permission.Propose));
            store.UndoRelationship(family, admin, legacyApproved);
            store.GrantScopedRelationshipPermission(family, branch, admin, author, Permission.Propose);
            store.GrantScopedRelationshipPermission(family, branch, admin, reviewer, Permission.Review);
            store.GrantScopedRelationshipPermission(family, branch, admin, reviewer, Permission.Undo);
            store.GrantScopedRelationshipPermission(family, branch, admin, undoer, Permission.Undo);
            Expect<UnauthorizedAccessException>(() => store.ProposeRelationship(family, author,
                Relationship.Create(first.Id, second.Id, RelationKind.Friend)));
            Expect<UnauthorizedAccessException>(() => store.ReviewRelationship(family, admin, legacyPending, true));
            Expect<UnauthorizedAccessException>(() => store.ReviewRelationship(family, admin, legacyPending, false));
            Expect<UnauthorizedAccessException>(() => store.UndoRelationship(family, admin, legacyApproved));
            Expect<UnauthorizedAccessException>(() => store.ReadRelationships(family, author));
            Expect<UnauthorizedAccessException>(() => store.ListPendingRelationships(family, admin));
            Expect<UnauthorizedAccessException>(() => store.ListApprovedRelationshipsForUndo(family, admin));
            Expect<UnauthorizedAccessException>(() => store.ProposeScopedRelationship(family, other, author,
                first.Id, second.Id, RelationKind.Friend, AssociationCategory.Social, "Synthetic"));
            Expect<UnauthorizedAccessException>(() => store.ProposeScopedRelationship(family, branch, author,
                first.Id, Guid.NewGuid(), RelationKind.Friend, AssociationCategory.Social, "Synthetic"));
            var id = Propose();
            var proposal = store.ListScopedPendingRelationships(family, branch, reviewer).Entries.Single();
            Check(proposal.ProposalId == id && proposal.Provenance!.Citation == "Synthetic Citation" &&
                proposal.Provenance.StartDate == new DateOnly(2001, 1, 1), "provenance roundtrip");
            Expect<UnauthorizedAccessException>(() => store.ReviewRelationship(family, admin, id, true));
            var beforeDenials = AuditCount(path);
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(family, branch, author, id, true));
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(family, other, reviewer, id, true));
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(family, branch, reviewer, Guid.NewGuid(), true));
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(Guid.NewGuid(), branch, reviewer, id, true));
            visibility.RevokeDisplayName(family, branch, admin, reviewer, second.Id);
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(family, branch, reviewer, id, true));
            Check(store.ListScopedPendingRelationships(family, branch, reviewer).Entries.Count == 0, "hidden pending");
            Check(AuditCount(path) == beforeDenials && Publications(path) == 0, "denials do not modify audit or graph");
            visibility.GrantDisplayName(family, branch, admin, reviewer, second.Id);
            using (var db = Connect(path))
            {
                Expect<SqliteException>(() => Execute(db, "DELETE FROM people WHERE id=$id;", second.Id));
                Expect<SqliteException>(() => Execute(db, "UPDATE people SET id='00000000-0000-0000-0000-000000000001' WHERE id=$id;", second.Id));
                var name = (byte[])Scalar(db, "SELECT encrypted_name FROM people WHERE id=$id;", second.Id)!;
                using var change = db.CreateCommand();
                change.CommandText = "UPDATE people SET encrypted_name=$name WHERE id=$id;";
                change.Parameters.AddWithValue("$name", new byte[] { 1 });
                change.Parameters.AddWithValue("$id", second.Id.ToString("D"));
                change.ExecuteNonQuery();
                Expect<InvalidDataException>(() => store.ReviewScopedRelationship(family, branch, reviewer, id, true));
                Expect<InvalidDataException>(() => Propose());
                Check(AuditCount(path) == beforeDenials && Publications(path) == 0,
                    "corrupt endpoint cannot be proposed or published");
                change.Parameters["$name"].Value = name;
                change.ExecuteNonQuery();
                Execute(db, "PRAGMA ignore_check_constraints=ON; UPDATE people SET living=2 WHERE id=$id;", second.Id);
                Expect<InvalidDataException>(() => Propose());
                Expect<InvalidDataException>(() => store.ReviewScopedRelationship(family, branch, reviewer, id, true));
                Check(AuditCount(path) == beforeDenials && Publications(path) == 0,
                    "invalid living flag cannot be proposed or published");
                Execute(db, "UPDATE people SET living=1 WHERE id=$id; PRAGMA ignore_check_constraints=OFF;", second.Id);
            }
            using (var db = Connect(path))
            {
                Execute(db, "UPDATE scoped_relationship_proposals SET payload=X'01' WHERE id=$id;", id);
            }
            var validPending = Propose();
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<100)
                    INSERT INTO scoped_relationship_proposals
                      (id,family_id,branch_id,relation_id,from_id,to_id,author,payload,state,reviewer,seal,identity_envelope)
                    SELECT printf('!invalid-%03d',n.i),family_id,branch_id,printf('!relation-%03d',n.i),
                      from_id,to_id,author,payload,state,reviewer,seal,identity_envelope
                    FROM scoped_relationship_proposals,n WHERE id=$id;
                    """;
                cmd.Parameters.AddWithValue("$id", id.ToString("D"));
                Check(cmd.ExecuteNonQuery() == 101, "insert malformed synthetic identifiers");
            }
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<100)
                    INSERT INTO scoped_relationship_proposals
                      (id,family_id,branch_id,relation_id,from_id,to_id,author,payload,state,reviewer,seal,identity_envelope)
                    SELECT printf('00000000-0000-0000-0000-%012x',n.i),family_id,branch_id,
                      printf('!forged-%03d',n.i),from_id,to_id,author,payload,state,reviewer,seal,identity_envelope
                    FROM scoped_relationship_proposals,n WHERE id=$id;
                    """;
                cmd.Parameters.AddWithValue("$id", id.ToString("D"));
                Check(cmd.ExecuteNonQuery() == 100, "insert forged synthetic identifiers");
            }
            var emptyPage = store.ListScopedPendingRelationships(family, branch, reviewer);
            Check(emptyPage.Entries.Count == 0 && emptyPage.NextCursor is not null,
                "empty unauthenticated page retains continuation");
            var continued = store.ListScopedPendingRelationships(family, branch, reviewer, emptyPage.NextCursor);
            Check(continued.Entries.Count == 2, "continuation reaches authenticated entries");
            var emptyCandidates = store.ListScopedRelationshipQuarantineCandidates(family, branch, reviewer);
            Check(emptyCandidates.ProposalIds.Count == 0 && emptyCandidates.NextCursor is not null,
                "empty quarantine page retains continuation");
            Check(store.ListScopedRelationshipQuarantineCandidates(family, branch, reviewer, emptyCandidates.NextCursor)
                .ProposalIds.SequenceEqual([id]), "quarantine continuation reaches valid candidate");
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM scoped_relationship_proposals WHERE relation_id GLOB '!forged-*';";
                Check(cmd.ExecuteNonQuery() == 100, "remove forged synthetic identifiers");
            }
            var inbox = store.ListScopedPendingRelationships(family, branch, reviewer).Entries;
            Check(inbox.Count == 2 && inbox.Single(x => x.ProposalId == id) is
                { ContentUnreadable: true, Provenance: null } &&
                inbox.Single(x => x.ProposalId == validPending) is
                { ContentUnreadable: false, Provenance: not null }, "corrupt and readable inbox entries");
            var firstCursor = inbox.Select(x => x.ProposalId).OrderBy(x => x.ToString("D"), StringComparer.Ordinal).First();
            Check(store.ListScopedPendingRelationships(family, branch, reviewer, firstCursor).Entries.Count == 1,
                "malformed identifiers do not affect inbox pagination");
            Check(store.ListScopedRelationshipQuarantineCandidates(family, branch, reviewer).ProposalIds.SequenceEqual([id]),
                "malformed identifiers do not hide quarantine candidates");
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM scoped_relationship_proposals WHERE id GLOB '!invalid-*';";
                Check(cmd.ExecuteNonQuery() == 101, "remove malformed synthetic identifiers");
            }
            store.ReviewScopedRelationship(family, branch, reviewer, validPending, false);
            Expect<InvalidDataException>(() => store.ReviewScopedRelationship(family, branch, reviewer, id, false));
            store.GrantScopedRelationshipPermission(family, branch, admin, author, Permission.Review);
            Expect<UnauthorizedAccessException>(() => store.QuarantineScopedRelationship(family, branch, author, id));
            Expect<UnauthorizedAccessException>(() => store.QuarantineScopedRelationship(family, other, reviewer, id));
            Check(store.ListScopedRelationshipQuarantineCandidates(family, branch, reviewer).ProposalIds.SequenceEqual([id]),
                "corrupt payload has content-free discovery");
            Check(store.ListScopedRelationshipQuarantineCandidates(family, branch, author).ProposalIds.Count == 0,
                "author cannot discover own quarantine candidate");
            byte[] identity;
            using (var db = Connect(path))
            {
                identity = (byte[])Scalar(db, "SELECT identity_envelope FROM scoped_relationship_proposals WHERE id=$id;", id)!;
                Execute(db, "UPDATE scoped_relationship_proposals SET identity_envelope=X'01' WHERE id=$id;", id);
            }
            Expect<InvalidDataException>(() => store.QuarantineScopedRelationship(family, branch, reviewer, id));
            Check(store.ListScopedRelationshipQuarantineCandidates(family, branch, reviewer).ProposalIds.Count == 0,
                "unauthenticated identity is not a candidate");
            using (var db = Connect(path))
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "UPDATE scoped_relationship_proposals SET identity_envelope=$identity WHERE id=$id;";
                cmd.Parameters.AddWithValue("$identity", identity);
                cmd.Parameters.AddWithValue("$id", id.ToString("D"));
                cmd.ExecuteNonQuery();
            }
            store.QuarantineScopedRelationship(family, branch, reviewer, id);
            Check(Publications(path) == 0, "quarantine publishes nothing");
            // A fresh proposal tests the remaining state transitions; corrupted rows stay quarantined.
            var approved = Propose();
            byte[] pendingSeal;
            using (var db = Connect(path))
                pendingSeal = (byte[])Scalar(db, "SELECT seal FROM scoped_relationship_proposals WHERE id=$id;", approved)!;
            store.RevokeScopedRelationshipPermission(family, branch, admin, reviewer, Permission.Review);
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(family, branch, reviewer, approved, true));
            store.GrantScopedRelationshipPermission(family, branch, admin, reviewer, Permission.Review);
            store.ReviewScopedRelationship(family, branch, reviewer, approved, true);
            Check(store.ListScopedRelationshipsForUndo(family, branch, undoer).Entries.Single().ProposalId == approved,
                "undo-only actor discovers authorized approved proposal");
            Expect<UnauthorizedAccessException>(() => store.ListScopedPendingRelationships(family, branch, undoer));
            visibility.RevokeDisplayName(family, branch, admin, undoer, second.Id);
            Check(store.ListScopedRelationshipsForUndo(family, branch, undoer).Entries.Count == 0, "undo inbox hides endpoints");
            visibility.GrantDisplayName(family, branch, admin, undoer, second.Id);
            Expect<UnauthorizedAccessException>(() => store.ReadRelationships(family, admin));
            Check(Publications(path) == 1, "approved scoped link");
            using (var db = Connect(path))
                Execute(db, "UPDATE scoped_relationship_publications SET payload=X'01' WHERE proposal_id=$id;", approved);
            Check(store.ListScopedRelationshipsForUndo(family, branch, undoer).Entries.Count == 0,
                "undo inbox excludes inconsistent publication");
            Expect<InvalidDataException>(() => store.UndoScopedRelationship(family, branch, reviewer, approved));
            using (var db = Connect(path))
                Execute(db, """
                    UPDATE scoped_relationship_publications SET payload=
                      (SELECT payload FROM scoped_relationship_proposals WHERE id=$id) WHERE proposal_id=$id;
                    """, approved);
            store.RevokeScopedRelationshipPermission(family, branch, admin, author, Permission.Propose);
            store.UndoScopedRelationship(family, branch, reviewer, approved);
            Check(Publications(path) == 0, "undo removes link");
            using (var db = Connect(path))
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE scoped_relationship_proposals SET state='Pending',reviewer=NULL,seal=$seal WHERE id=$id;";
                cmd.Parameters.AddWithValue("$seal", pendingSeal);
                cmd.Parameters.AddWithValue("$id", approved.ToString("D"));
                cmd.ExecuteNonQuery();
                cmd.CommandText = "INSERT INTO scoped_relationship_events(proposal_id,seal) VALUES($id,$seal);";
                cmd.ExecuteNonQuery();
            }
            var beforeReplay = AuditCount(path);
            Expect<InvalidDataException>(() => store.ReviewScopedRelationship(family, branch, reviewer, approved, true));
            Check(AuditCount(path) == beforeReplay && Publications(path) == 0, "replay changes neither graph nor audit");
            store.GrantScopedRelationshipPermission(family, branch, admin, author, Permission.Propose);
            var rejected = Propose();
            store.RevokeMember(family, admin, author);
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(family, branch, reviewer, rejected, true));
            store.ReviewScopedRelationship(family, branch, reviewer, rejected, false);
            Join(store, family, admin, author);
            Expect<UnauthorizedAccessException>(() => Propose());
            store.GrantScopedRelationshipPermission(family, branch, admin, author, Permission.Propose);
            foreach (var person in new[] { first, second })
            {
                visibility.GrantPerson(family, branch, admin, author, person.Id);
                visibility.GrantDisplayName(family, branch, admin, author, person.Id);
            }
            using (var reopened = new SqliteFamilyStore(path, key))
            {
                Check(reopened.IsActiveMember(family, reviewer) && Publications(path) == 0, "durable isolation");
                Expect<UnauthorizedAccessException>(() => reopened.ReviewRelationship(family, admin, legacyPending, true));
                Expect<UnauthorizedAccessException>(() => reopened.UndoRelationship(family, admin, legacyApproved));
                Expect<UnauthorizedAccessException>(() => reopened.ListPendingRelationships(family, admin));
            }

            var concurrent = Propose();
            var successes = 0;
            var unavailable = 0;
            Parallel.For(0, 2, _ =>
            {
                try { store.ReviewScopedRelationship(family, branch, reviewer, concurrent, true); Interlocked.Increment(ref successes); }
                catch (InvalidOperationException ex) when (ex.Message == "Proposal unavailable.")
                {
                    Interlocked.Increment(ref unavailable);
                }
            });
            Check(successes == 1 && unavailable == 1 && Publications(path) == 1, "one concurrent approval");
            store.UndoScopedRelationship(family, branch, reviewer, concurrent);
            var one = Propose();
            var two = Propose();
            store.ReviewScopedRelationship(family, branch, reviewer, one, true);
            store.ReviewScopedRelationship(family, branch, reviewer, two, true);
            Check(Publications(path) == 2,
                "multiple independent typed links");
            store.UndoScopedRelationship(family, branch, reviewer, one);
            store.UndoScopedRelationship(family, branch, reviewer, two);

            store.GrantScopedPersonPermission(family, branch, admin, author, PersonPermission.CreatePerson);
            store.GrantScopedPersonPermission(family, branch, admin, reviewer, PersonPermission.ReviewPerson);
            store.GrantScopedPersonPermission(family, branch, admin, reviewer, PersonPermission.UndoPerson);
            var personProposal = store.ProposeScopedPerson(family, branch, author, "Synthetic Scoped Endpoint");
            var scopedPerson = store.ListScopedPendingPeople(family, branch, reviewer).Single().Person;
            store.ReviewScopedPerson(family, branch, reviewer, personProposal, true);
            foreach (var member in new[] { author, reviewer })
            {
                visibility.GrantPerson(family, branch, admin, member, scopedPerson.Id);
                visibility.GrantDisplayName(family, branch, admin, member, scopedPerson.Id);
            }
            Expect<UnauthorizedAccessException>(() => store.ProposeRelationship(family, author,
                Relationship.Create(scopedPerson.Id, first.Id, RelationKind.Friend)));
            var dependency = store.ProposeScopedRelationship(family, branch, author, scopedPerson.Id, first.Id,
                RelationKind.Friend, AssociationCategory.Social, "Synthetic");
            foreach (var member in new[] { author, reviewer })
                visibility.RevokePerson(family, branch, admin, member, scopedPerson.Id);
            Expect<InvalidOperationException>(() => store.UndoScopedPerson(family, branch, reviewer, personProposal));
            foreach (var member in new[] { author, reviewer })
            {
                visibility.GrantPerson(family, branch, admin, member, scopedPerson.Id);
                visibility.GrantDisplayName(family, branch, admin, member, scopedPerson.Id);
            }
            store.ReviewScopedRelationship(family, branch, reviewer, dependency, false);
            var corruptedDependency = store.ProposeScopedRelationship(family, branch, author, scopedPerson.Id, first.Id,
                RelationKind.Friend, AssociationCategory.Social, "Synthetic");
            using (var db = Connect(path))
                Execute(db, "UPDATE scoped_relationship_proposals SET payload=X'01' WHERE id=$id;", corruptedDependency);
            store.QuarantineScopedRelationship(family, branch, reviewer, corruptedDependency);
            foreach (var member in new[] { author, reviewer })
                visibility.RevokePerson(family, branch, admin, member, scopedPerson.Id);
            store.UndoScopedPerson(family, branch, reviewer, personProposal);
            var secondPersonProposal = store.ProposeScopedPerson(family, branch, author, "Synthetic Second Endpoint");
            var secondScopedPerson = store.ListScopedPendingPeople(family, branch, reviewer).Single().Person;
            store.ReviewScopedPerson(family, branch, reviewer, secondPersonProposal, true);
            foreach (var member in new[] { author, reviewer })
            {
                visibility.GrantPerson(family, branch, admin, member, secondScopedPerson.Id);
                visibility.GrantDisplayName(family, branch, admin, member, secondScopedPerson.Id);
            }
            var approvedDependency = store.ProposeScopedRelationship(family, branch, author, secondScopedPerson.Id, first.Id,
                RelationKind.Friend, AssociationCategory.Social, "Synthetic");
            store.ReviewScopedRelationship(family, branch, reviewer, approvedDependency, true);
            store.UndoScopedRelationship(family, branch, reviewer, approvedDependency);
            foreach (var member in new[] { author, reviewer })
                visibility.RevokePerson(family, branch, admin, member, secondScopedPerson.Id);
            store.UndoScopedPerson(family, branch, reviewer, secondPersonProposal);
            var awaitingReview = Propose();
            store.RevokeMember(family, admin, reviewer);
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(family, branch, reviewer, awaitingReview, false));
            Join(store, family, admin, reviewer);
            Expect<UnauthorizedAccessException>(() => store.ReviewScopedRelationship(family, branch, reviewer, awaitingReview, false));
            store.RevokeScopedRelationshipPermission(family, branch, admin, author, Permission.Propose);
            store.RevokeScopedRelationshipPermission(family, branch, admin, author, Permission.Review);
            Expect<UnauthorizedAccessException>(() => store.ProposeRelationship(family, author,
                Relationship.Create(first.Id, second.Id, RelationKind.Friend)));
            Expect<UnauthorizedAccessException>(() => store.ReadRelationships(family, author));
            Console.WriteLine("PASS SQLite scoped relationship review, grants, provenance, isolation and undo");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(directory, true);
        }
    }

    private static void Join(SqliteFamilyStore store, Guid family, Guid admin, Guid member)
    {
        var now = DateTimeOffset.UtcNow;
        var invite = store.IssueInvitation(family, admin, member, now.AddHours(1), now);
        store.AcceptInvitation(family, member, invite, now);
        store.ApproveMember(family, admin, member);
    }

    private static SqliteConnection Connect(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        return db;
    }

    private static object? Scalar(SqliteConnection db, string sql, Guid id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        return cmd.ExecuteScalar();
    }

    private static long Publications(string path)
    {
        using var db = Connect(path);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scoped_relationship_publications;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static long AuditCount(string path)
    {
        using var db = Connect(path);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection db, string sql, Guid id)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        Check(cmd.ExecuteNonQuery() == 1, "tamper affected one row");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
