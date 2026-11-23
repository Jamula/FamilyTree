using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

internal static class SqliteRelationshipAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var folder = Path.Join(Path.GetTempPath(),
            "familytree-relations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var database = Path.Join(folder, "fictional.db");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var other = Guid.NewGuid();
        var personA = new Person(Guid.NewGuid(), "Fictional Silver");
        var personB = new Person(Guid.NewGuid(), "Fictional Stone");
        var edgeA = Relationship.Create(personA.Id, personB.Id, RelationKind.Friend,
            "synthetic-neighbor-private");
        var edgeB = Relationship.Create(personA.Id, personB.Id, RelationKind.Mentor,
            "synthetic-mentor-private");
        try
        {
            using (var store = new SqliteFamilyStore(database, key))
            {
                store.CreateFictionalFamily(family, admin);
                var invite = store.IssueInvitation(family, admin, actor,
                    DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
                store.AcceptInvitation(family, actor, invite, DateTimeOffset.UtcNow);
                store.ApproveMember(family, admin, actor);
                foreach (var person in new[] {personA, personB})
                {
                    var proposal = store.ProposePerson(family, actor, person);
                    store.ReviewPerson(family, admin, proposal, true);
                }
                Check(store.ReadPeople(family, actor).Count == 2,
                    "independent Person approval creates relationship endpoints");
            }
            using (var store = new SqliteFamilyStore(database, key))
            {
                Expect<UnauthorizedAccessException>(() => store.ReadRelationships(
                    Guid.NewGuid(), actor), "cross-family relationships denied");
                Expect<UnauthorizedAccessException>(() => store.ReadRelationships(
                    family, other), "unknown viewer denied");
                Expect<UnauthorizedAccessException>(() => store.ProposeRelationship(
                    family, other, edgeA), "unjoined author denied");
                Expect<ArgumentException>(() => store.ProposeRelationship(family,
                    actor, edgeA with { Label = "Bad\nLabel" }), "control label denied");
                Expect<ArgumentException>(() => store.ProposeRelationship(family,
                    actor, edgeA with { Label = new string('x', 201) }),
                    "oversized label denied");
                Expect<ArgumentException>(() => store.ProposeRelationship(family,
                    actor, edgeA with { Kind = (RelationKind)999 }),
                    "unknown relation kind denied");
                Expect<ArgumentException>(() => store.ProposeRelationship(family,
                    actor, edgeA with { FromPersonId = edgeA.ToPersonId }),
                    "self relation input denied");
                Expect<InvalidOperationException>(() => store.ProposeRelationship(
                    family, actor, edgeA with { ToPersonId = Guid.NewGuid() }),
                    "missing approved endpoint denied");

                var pA = store.ProposeRelationship(family, actor, edgeA);
                var pB = store.ProposeRelationship(family, actor, edgeB);
                Check(store.ReadRelationships(family, actor).Count == 0,
                    "pending relationships hidden");
                Check(store.ListPendingRelationships(family, admin).Count == 2,
                    "moderator can read exact pending relationships");
                Expect<UnauthorizedAccessException>(() => store.ListPendingRelationships(
                    family, actor), "non-admin cannot inspect pending labels");
                Expect<UnauthorizedAccessException>(() => store.ListPendingRelationships(
                    Guid.NewGuid(), admin), "other family pending hidden");
                Expect<InvalidOperationException>(() => store.ProposeRelationship(
                    family, actor, edgeA), "duplicate pending edge denied");
                Expect<UnauthorizedAccessException>(() => store.ReviewRelationship(
                    family, actor, pA, true), "member self-approval denied");
                Expect<InvalidOperationException>(() => store.ReviewRelationship(
                    family, admin, Guid.NewGuid(), true), "unknown proposal hidden");
                Expect<UnauthorizedAccessException>(() => store.ReviewRelationship(
                    Guid.NewGuid(), admin, pA, true), "cross-family proposal denied");
                store.ReviewRelationship(family, admin, pA, true);
                store.ReviewRelationship(family, admin, pB, true);
                Check(store.ReadRelationships(family, actor).Select(x=>x.Id).ToHashSet()
                    .SetEquals(new[] {edgeA.Id, edgeB.Id}),
                    "multiple independent social links retained");
                Check(store.ReadRelationships(family, actor).Any(x=>x==edgeA) &&
                      store.ReadRelationships(family, actor).Any(x=>x==edgeB),
                    "encrypted labels and independent kinds roundtrip");
                Expect<InvalidOperationException>(() => store.ReviewRelationship(
                    family, admin, pA, true), "review replay denied");
                Expect<InvalidOperationException>(() => store.ProposeRelationship(
                    family, actor, edgeA), "duplicate approved edge denied");
                Check(store.CountAudit(family, admin) == 5,
                    "membership approval, people reviews and link approvals audited");
                var adminOwned = store.ProposeRelationship(family, admin,
                    Relationship.Create(personA.Id,personB.Id,RelationKind.Other,
                        "fictional-unreviewed-admin"));
                Check(!store.ListPendingRelationships(family, admin).Any(x =>
                    x.ProposalId == adminOwned),
                    "self-submitted items do not occupy independent review inbox");
                Expect<UnauthorizedAccessException>(() => store.ReviewRelationship(
                    family, admin, adminOwned, true),
                    "administrator cannot approve self-authored link");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(database)).Contains(
                    "synthetic-neighbor-private", StringComparison.Ordinal),
                    "relationship label encrypted at rest");
            }
            using (var store = new SqliteFamilyStore(database, key))
            {
                Check(store.ReadRelationships(family, actor).Count == 2,
                    "approved links survive restart");
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = database, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var tamper = connection.CreateCommand();
                    tamper.CommandText = """
                        UPDATE relationships SET kind='Mentor' WHERE id=$id;
                        """;
                    tamper.Parameters.AddWithValue("$id", edgeA.Id.ToString("D"));
                    Check(tamper.ExecuteNonQuery() == 1,
                        "one approved relation metadata row tampered");
                    Expect<InvalidDataException>(() => store.ReadRelationships(
                        family, actor), "modified valid kind must invalidate AES-GCM AAD");
                    using var restore = connection.CreateCommand();
                    restore.CommandText = """
                        UPDATE relationships SET kind='Friend' WHERE id=$id;
                        """;
                    restore.Parameters.AddWithValue("$id",edgeA.Id.ToString("D"));
                    Check(restore.ExecuteNonQuery() == 1, "original metadata restored");
                }
                Check(store.ReadRelationships(family, actor).Count == 2,
                    "restored authentic metadata remains readable");
                var review = store.ListPendingRelationships(family, admin);
                Check(review.Count == 0,"approved proposals excluded from pending inbox");
                var parent = Relationship.Create(personA.Id, personB.Id, RelationKind.Parent);
                var pending = store.ProposeRelationship(family, actor, parent);
                store.ReviewRelationship(family, admin, pending, false);
                Check(store.ReadRelationships(family, actor).Count == 2,
                    "rejected relationship cannot publish");
                var currentLinks = store.ReadRelationships(family, actor);
                var target = currentLinks.Single(x=>x.Kind==RelationKind.Friend);
                var otherId = store.ReadRelationships(family, actor)
                    .Single(x=>x.Kind==RelationKind.Mentor).Id;
                // The approved proposal ID is known from first submission, so
                // look it up via a second fictional test proposal for undo.
                var newEdge = Relationship.Create(personA.Id, personB.Id,
                    RelationKind.Neighbor, "fictional-reversible");
                var undoProposal = store.ProposeRelationship(family, actor, newEdge);
                store.ReviewRelationship(family, admin, undoProposal, true);
                store.UndoRelationship(family, admin, undoProposal);
                Check(store.ReadRelationships(family, actor).Count == 2 &&
                    store.ReadRelationships(family, actor).Any(x=>x.Id==target.Id) &&
                    store.ReadRelationships(family, actor).Any(x=>x.Id==otherId),
                    "undo removes only its own reviewed link");
                Expect<InvalidOperationException>(() => store.UndoRelationship(
                    family, admin, undoProposal), "undo replay denied");
                var damaged = Relationship.Create(personA.Id, personB.Id,
                    RelationKind.Neighbor, "fictional-damaged-review");
                var corruptProposal = store.ProposeRelationship(family, actor, damaged);
                using (var conn = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = database, Pooling = false }.ToString()))
                {
                    conn.Open();
                    using var update = conn.CreateCommand();
                    update.CommandText = """
                        UPDATE relationship_proposals SET encrypted_label=X'01'
                        WHERE id=$id;
                        """;
                    update.Parameters.AddWithValue("$id", corruptProposal.ToString("D"));
                    Check(update.ExecuteNonQuery() == 1, "pending ciphertext tampered");
                }
                Check(store.ListPendingRelationships(family, admin).Any(x =>
                    x.ProposalId == corruptProposal && x.ContentUnreadable &&
                    x.Relationship is null),
                    "moderator sees safe unusable proposal ID without private content");
                Expect<InvalidDataException>(() => store.ReviewRelationship(
                    family, admin, corruptProposal, true),
                    "corrupt ciphertext cannot become approved");
                store.ReviewRelationship(family, admin, corruptProposal, false);
                Check(store.ReadRelationships(family, admin).Count == 2,
                    "rejecting bad proposal preserves healthy approved links");
                Expect<UnauthorizedAccessException>(() => store.UndoRelationship(
                    family, actor, Guid.NewGuid()), "unauthorized undo denied");
                store.RevokeMember(family, admin, actor);
                Expect<UnauthorizedAccessException>(() => store.ReadRelationships(
                    family, actor), "member revocation blocks saved links");
                Expect<UnauthorizedAccessException>(() => store.ProposeRelationship(
                    family, actor, edgeB), "revoked member cannot write");
                Check(store.ReadRelationships(family, admin).Count==2,
                    "administrator retains same-family records");
            }
            Console.WriteLine("PASS durable typed links, review, undo, restart, privacy and negative tests");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static void Check(bool value,string why)
    {if(!value)throw new InvalidOperationException(why);}
    private static void Expect<T>(Action op,string why) where T:Exception
    {
        var matched=false;
        try {op();} catch(T) {matched=true;}
        if(!matched)throw new InvalidOperationException(why);
    }
}
