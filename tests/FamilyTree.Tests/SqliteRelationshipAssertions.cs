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
        var otherFamily = Guid.NewGuid();
        var otherAdmin = Guid.NewGuid();
        var otherActor = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var other = Guid.NewGuid();
        var personA = new Person(Guid.NewGuid(), "Fictional Silver");
        var personB = new Person(Guid.NewGuid(), "Fictional Stone");
        var edgeA = Relationship.Create(personA.Id, personB.Id, RelationKind.Friend,
            "synthetic-neighbor-private");
        var edgeB = Relationship.Create(personA.Id, personB.Id, RelationKind.Mentor,
            "synthetic-mentor-private");
        var originalProposal = Guid.Empty;
        try
        {
            using (var store = new SqliteFamilyStore(database, key))
            {
                store.CreateFictionalFamily(family, admin);
                store.CreateFictionalFamily(otherFamily, otherAdmin);
                var otherInvitation = store.IssueInvitation(otherFamily, otherAdmin,
                    otherActor, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
                store.AcceptInvitation(otherFamily, otherActor, otherInvitation,
                    DateTimeOffset.UtcNow);
                store.ApproveMember(otherFamily, otherAdmin, otherActor);
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
                    Guid.NewGuid(), actor), "unknown family relationships denied");

                var foreignFamily = Guid.NewGuid();
                var foreignAdmin = Guid.NewGuid();
                var foreignAuthor = Guid.NewGuid();
                store.CreateFictionalFamily(foreignFamily, foreignAdmin);
                var now = DateTimeOffset.UtcNow;
                var foreignInvite = store.IssueInvitation(foreignFamily, foreignAdmin,
                    foreignAuthor, now.AddDays(1), now);
                store.AcceptInvitation(foreignFamily, foreignAuthor, foreignInvite, now);
                store.ApproveMember(foreignFamily, foreignAdmin, foreignAuthor);
                var foreignA = new Person(Guid.NewGuid(), "Fictional Ash");
                var foreignB = new Person(Guid.NewGuid(), "Fictional Birch");
                foreach (var person in new[] {foreignA, foreignB})
                    store.ReviewPerson(foreignFamily, foreignAdmin,
                        store.ProposePerson(foreignFamily, foreignAuthor, person), true);
                var foreignEdge = Relationship.Create(foreignA.Id, foreignB.Id,
                    RelationKind.Friend, "fictional-foreign-link");
                var foreignProposal = store.ProposeRelationship(
                    foreignFamily, foreignAuthor, foreignEdge);
                var localAuditBefore = store.CountAudit(family, admin);
                var foreignAuditBefore = store.CountAudit(foreignFamily, foreignAdmin);

                Expect<UnauthorizedAccessException>(() => store.ReadRelationships(
                    foreignFamily, actor), "existing foreign family read denied");
                Expect<InvalidOperationException>(() => store.ProposeRelationship(
                    family, actor, foreignEdge), "foreign approved endpoints denied");
                Expect<InvalidOperationException>(() => store.ReviewRelationship(
                    family, admin, foreignProposal, true), "foreign proposal approval denied");
                Expect<InvalidOperationException>(() => store.ReviewRelationship(
                    family, admin, foreignProposal, false), "foreign proposal rejection denied");
                Check(store.ReadRelationships(family, actor).Count == 0 &&
                    store.ReadRelationships(foreignFamily, foreignAuthor).Count == 0 &&
                    store.ListPendingRelationships(family, admin).Count == 0 &&
                    store.ListPendingRelationships(foreignFamily, foreignAdmin)
                        .Single().ProposalId == foreignProposal &&
                    store.CountAudit(family, admin) == localAuditBefore &&
                    store.CountAudit(foreignFamily, foreignAdmin) == foreignAuditBefore,
                    "denied foreign proposals and reviews preserve both families");

                store.ReviewRelationship(foreignFamily, foreignAdmin, foreignProposal, true);
                Check(store.ReadRelationships(foreignFamily, foreignAuthor)
                    .Single() == foreignEdge &&
                    store.CountAudit(foreignFamily, foreignAdmin) == foreignAuditBefore + 1,
                    "foreign relationship approved only within its own family");
                Expect<InvalidOperationException>(() => store.UndoRelationship(
                    family, admin, foreignProposal), "foreign approved proposal undo denied");
                Check(store.ReadRelationships(family, actor).Count == 0 &&
                    store.ReadRelationships(foreignFamily, foreignAuthor)
                        .Single() == foreignEdge &&
                    store.ListApprovedRelationshipsForUndo(foreignFamily, foreignAdmin)
                        .Single().ProposalId == foreignProposal &&
                    store.CountAudit(family, admin) == localAuditBefore &&
                    store.CountAudit(foreignFamily, foreignAdmin) == foreignAuditBefore + 1,
                    "denied foreign undo preserves both graphs and audits");
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
                originalProposal = pA;
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
                var foreignAuditBefore = store.CountAudit(otherFamily, otherAdmin);
                Expect<InvalidOperationException>(() => store.ProposeRelationship(
                    otherFamily, otherActor, edgeA),
                    "active other-family member cannot propose links to foreign people");
                Expect<InvalidOperationException>(() => store.ReviewRelationship(
                    otherFamily, otherAdmin, pA, true),
                    "active other-family administrator cannot review foreign proposal");
                Expect<InvalidOperationException>(() => store.UndoRelationship(
                    otherFamily, otherAdmin, pA),
                    "active other-family administrator cannot undo foreign proposal");
                Check(store.ReadRelationships(otherFamily, otherActor).Count == 0 &&
                    store.CountAudit(otherFamily, otherAdmin) == foreignAuditBefore,
                    "foreign graph and audit unchanged after cross-family attacks");
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
                var auditBeforeTamper = store.CountAudit(family, admin);
                using (var tamperProposal = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = database, Pooling = false }.ToString()))
                {
                    tamperProposal.Open();
                    using var change = tamperProposal.CreateCommand();
                    change.CommandText = """
                        UPDATE relationship_proposals SET relation_id=$other
                        WHERE family_id=$f AND id=$id;
                        """;
                    change.Parameters.AddWithValue("$other", edgeB.Id.ToString("D"));
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$id", originalProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1,
                        "tamper only the approved proposal's relation ID");
                    Expect<InvalidDataException>(() => store.UndoRelationship(
                        family, admin, originalProposal),
                        "forged undo target cannot delete an unrelated approved link");
                    Check(store.ReadRelationships(family, admin).Count == 2 &&
                          store.CountAudit(family, admin) == auditBeforeTamper,
                        "tampered undo neither alters graph nor appends audit");
                    change.CommandText = """
                        UPDATE relationship_proposals SET relation_id=$original
                        WHERE family_id=$f AND id=$id;
                        """;
                    change.Parameters.AddWithValue("$original", edgeA.Id.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1,
                        "restore authenticated proposal mapping");
                }
                var undoInbox = store.ListApprovedRelationshipsForUndo(family, admin);
                Check(undoInbox.Count == 2 &&
                    undoInbox.All(x => x.Relationship is not null &&
                        !x.ContentUnreadable && x.SubmittedBy == actor),
                    "independent moderator rediscovers exact approved proposals after restart");
                Expect<UnauthorizedAccessException>(() =>
                    store.ListApprovedRelationshipsForUndo(family, actor),
                    "ordinary member cannot enumerate approved undo inbox");
                var nullLabel = Relationship.Create(personA.Id, personB.Id,
                    RelationKind.Spouse, null);
                var emptyLabel = Relationship.Create(personA.Id, personB.Id,
                    RelationKind.Partner, "");
                var nullProposal = store.ProposeRelationship(family, actor, nullLabel);
                var emptyProposal = store.ProposeRelationship(family, actor, emptyLabel);
                store.ReviewRelationship(family, admin, nullProposal, true);
                store.ReviewRelationship(family, admin, emptyProposal, true);
                Check(store.ReadRelationships(family, actor)
                    .Single(x => x.Id == nullLabel.Id).Label is null &&
                    store.ReadRelationships(family, actor)
                    .Single(x => x.Id == emptyLabel.Id).Label == "",
                    "encrypted null and explicit empty labels retain distinct meaning");
                Check(store.ListApprovedRelationshipsForUndo(family, admin)
                    .Any(x => x.ProposalId == emptyProposal &&
                        x.Relationship?.Label == ""),
                    "approved undo inbox retains explicit empty label");
                store.UndoRelationship(family, admin, nullProposal);
                store.UndoRelationship(family, admin, emptyProposal);
                Check(store.ReadRelationships(family, actor).Count == 2,
                    "temporary optional-label links reversed without touching original links");

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
                Expect<UnauthorizedAccessException>(() =>
                    store.ListApprovedRelationshipsForUndo(family, actor),
                    "revoked member cannot enumerate approved undo inbox");
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
