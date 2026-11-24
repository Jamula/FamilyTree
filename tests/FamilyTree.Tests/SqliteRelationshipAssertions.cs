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
        var otherPersonA = new Person(Guid.NewGuid(), "Fictional Cedar");
        var otherPersonB = new Person(Guid.NewGuid(), "Fictional Harbor");
        var edgeA = Relationship.Create(personA.Id, personB.Id, RelationKind.Friend,
            "synthetic-neighbor-private");
        var edgeB = Relationship.Create(personA.Id, personB.Id, RelationKind.Mentor,
            "synthetic-mentor-private");
        var originalProposal = Guid.Empty;
        var otherProposal = Guid.Empty;
        try
        {
            using (var priorSchema = new SqliteConnection(
                new SqliteConnectionStringBuilder {
                    DataSource = database, Pooling = false }.ToString()))
            {
                priorSchema.Open();
                using var createAudit = priorSchema.CreateCommand();
                createAudit.CommandText = """
                    CREATE TABLE audit (
                        seq INTEGER PRIMARY KEY AUTOINCREMENT,
                        family_id TEXT NOT NULL, subject_id TEXT NOT NULL,
                        reviewer TEXT NOT NULL, action TEXT NOT NULL
                    );
                    """;
                createAudit.ExecuteNonQuery();
            }
            using (var store = new SqliteFamilyStore(database, key))
            {
                store.CreateFictionalFamily(family, admin);
                store.CreateFictionalFamily(otherFamily, otherAdmin);
                var otherInvitation = store.IssueInvitation(otherFamily, otherAdmin,
                    otherActor, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
                store.AcceptInvitation(otherFamily, otherActor, otherInvitation,
                    DateTimeOffset.UtcNow);
                store.ApproveMember(otherFamily, otherAdmin, otherActor);
                foreach (var person in new[] {otherPersonA, otherPersonB})
                {
                    var proposal = store.ProposePerson(otherFamily, otherActor, person);
                    store.ReviewPerson(otherFamily, otherAdmin, proposal, true);
                }
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
                otherProposal = pB;
                var otherApprovedEdge = Relationship.Create(otherPersonA.Id,
                    otherPersonB.Id, RelationKind.Friend, "fictional second-family link");
                var otherApprovedProposal = store.ProposeRelationship(otherFamily,
                    otherActor, otherApprovedEdge);
                store.ReviewRelationship(otherFamily, otherAdmin,
                    otherApprovedProposal, true);
                var otherPendingEdge = Relationship.Create(otherPersonA.Id,
                    otherPersonB.Id, RelationKind.Neighbor, "fictional second-family pending");
                var otherPendingProposal = store.ProposeRelationship(otherFamily,
                    otherActor, otherPendingEdge);
                Check(store.ReadRelationships(family, actor).Count == 0,
                    "pending relationships hidden");
                Check(store.ListPendingRelationships(family, admin).Count == 2,
                    "moderator can read exact pending relationships");
                Check(store.ReadRelationships(otherFamily, otherActor)
                        .SequenceEqual(new[] {otherApprovedEdge}) &&
                      store.ListPendingRelationships(otherFamily, otherAdmin)
                        .Select(x => x.ProposalId).SequenceEqual(new[] {otherPendingProposal}),
                    "second populated family has independent published and pending links");
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
                var familyAPendingBeforeForeignReview = store.ListPendingRelationships(
                    family, admin).Select(x => x.ProposalId).ToArray();
                var familyBPendingBeforeForeignReview = store.ListPendingRelationships(
                    otherFamily, otherAdmin).Select(x => x.ProposalId).ToArray();
                var familyAAuditBeforeForeignReview = store.CountAudit(family, admin);
                var familyBAuditBeforeForeignReview = store.CountAudit(otherFamily, otherAdmin);
                Expect<InvalidOperationException>(() => store.ReviewRelationship(
                    otherFamily, otherAdmin, pA, true),
                    "authorized second-family administrator cannot review foreign pending proposal");
                Check(store.ListPendingRelationships(family, admin)
                        .Select(x => x.ProposalId).SequenceEqual(familyAPendingBeforeForeignReview) &&
                      store.ListPendingRelationships(otherFamily, otherAdmin)
                        .Select(x => x.ProposalId).SequenceEqual(familyBPendingBeforeForeignReview) &&
                      store.CountAudit(family, admin) == familyAAuditBeforeForeignReview &&
                      store.CountAudit(otherFamily, otherAdmin) == familyBAuditBeforeForeignReview,
                    "foreign pending review preserves both families' proposals and audits");
                store.ReviewRelationship(family, admin, pA, true);
                store.ReviewRelationship(family, admin, pB, true);
                Expect<InvalidOperationException>(() => store.ProposeRelationship(
                    otherFamily, otherActor, edgeA),
                    "active other-family member cannot propose links to foreign people");
                var familyAGraphBeforeForeignUndo = store.ReadRelationships(family, admin);
                var familyBGraphBeforeForeignUndo = store.ReadRelationships(otherFamily, otherAdmin);
                var familyAAuditBeforeForeignUndo = store.CountAudit(family, admin);
                var familyBAuditBeforeForeignUndo = store.CountAudit(otherFamily, otherAdmin);
                Expect<InvalidOperationException>(() => store.UndoRelationship(
                    otherFamily, otherAdmin, pA),
                    "active other-family administrator cannot undo foreign proposal");
                Expect<InvalidOperationException>(() => store.UndoRelationship(
                    family, admin, otherApprovedProposal),
                    "first-family administrator cannot undo second-family proposal");
                Check(store.ReadRelationships(family, admin).SequenceEqual(familyAGraphBeforeForeignUndo) &&
                    store.ReadRelationships(otherFamily, otherAdmin).SequenceEqual(familyBGraphBeforeForeignUndo) &&
                    store.ListPendingRelationships(otherFamily, otherAdmin)
                        .Select(x => x.ProposalId).SequenceEqual(new[] {otherPendingProposal}) &&
                    store.CountAudit(family, admin) == familyAAuditBeforeForeignUndo &&
                    store.CountAudit(otherFamily, otherAdmin) == familyBAuditBeforeForeignUndo,
                    "foreign approved undo preserves both populated graphs, pending proposal and audits");
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
                var auditBeforeSubmitterTamper = store.CountAudit(family, admin);
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = database, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var change = connection.CreateCommand();
                    change.CommandText = """
                        UPDATE relationship_proposals SET submitter=$forged
                        WHERE family_id=$f AND id=$id;
                        """;
                    change.Parameters.AddWithValue("$forged", actor.ToString("D"));
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$id", adminOwned.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "tamper only proposal submitter");
                    Expect<InvalidDataException>(() => store.ReviewRelationship(
                        family, admin, adminOwned, true),
                        "submitter-only substitution fails authenticated identity before review");
                    Expect<InvalidDataException>(() => store.ReviewRelationship(
                        family, admin, adminOwned, false),
                        "submitter-only substitution fails authenticated identity before rejection");
                    Check(store.CountAudit(family, admin) == auditBeforeSubmitterTamper,
                        "submitter substitution does not append an audit event");
                    change.CommandText = """
                        UPDATE relationship_proposals SET submitter=$original
                        WHERE family_id=$f AND id=$id;
                        """;
                    change.Parameters.AddWithValue("$original", admin.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "restore authenticated submitter");
                }
                var identityTarget = store.ProposeRelationship(family, actor,
                    Relationship.Create(personA.Id, personB.Id,
                        RelationKind.Neighbor, "fictional-envelope-target"));
                var identitySource = store.ProposeRelationship(family, actor,
                    Relationship.Create(personA.Id, personB.Id,
                        RelationKind.Parent, "fictional-envelope-source"));
                var auditBeforeEnvelopeSubstitution = store.CountAudit(family, admin);
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = database, Pooling = false }.ToString()))
                {
                    connection.Open();
                    string relationId, fromId, toId, kind, submitter;
                    byte[] label, identity;
                    using (var original = connection.CreateCommand())
                    {
                        original.CommandText = """
                            SELECT relation_id,from_id,to_id,kind,encrypted_label,
                                   submitter,identity_envelope
                            FROM relationship_proposals WHERE family_id=$f AND id=$id;
                            """;
                        original.Parameters.AddWithValue("$f", family.ToString("D"));
                        original.Parameters.AddWithValue("$id", identityTarget.ToString("D"));
                        using var reader = original.ExecuteReader();
                        Check(reader.Read(), "read the original pending proposal envelope");
                        relationId = reader.GetString(0);
                        fromId = reader.GetString(1);
                        toId = reader.GetString(2);
                        kind = reader.GetString(3);
                        label = reader.GetFieldValue<byte[]>(4);
                        submitter = reader.GetString(5);
                        identity = reader.GetFieldValue<byte[]>(6);
                    }
                    using var change = connection.CreateCommand();
                    change.CommandText = """
                        UPDATE relationship_proposals SET
                            relation_id=(SELECT relation_id FROM relationship_proposals
                                         WHERE family_id=$f AND id=$source),
                            from_id=(SELECT from_id FROM relationship_proposals
                                     WHERE family_id=$f AND id=$source),
                            to_id=(SELECT to_id FROM relationship_proposals
                                   WHERE family_id=$f AND id=$source),
                            kind=(SELECT kind FROM relationship_proposals
                                  WHERE family_id=$f AND id=$source),
                            encrypted_label=(SELECT encrypted_label
                                             FROM relationship_proposals
                                             WHERE family_id=$f AND id=$source),
                            submitter=(SELECT submitter FROM relationship_proposals
                                       WHERE family_id=$f AND id=$source),
                            identity_envelope=(SELECT identity_envelope
                                               FROM relationship_proposals
                                               WHERE family_id=$f AND id=$source)
                        WHERE family_id=$f AND id=$target;
                        """;
                    change.Parameters.AddWithValue("$source", identitySource.ToString("D"));
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$target", identityTarget.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1,
                        "substitute complete pending proposal envelope");
                    Expect<InvalidDataException>(() => store.ReviewRelationship(
                        family, admin, identityTarget, true),
                        "proposal-ID envelope substitution cannot be approved");
                    Expect<InvalidDataException>(() => store.ReviewRelationship(
                        family, admin, identityTarget, false),
                        "proposal-ID envelope substitution cannot be rejected");
                    Check(store.CountAudit(family, admin) == auditBeforeEnvelopeSubstitution,
                        "failed proposal-envelope substitutions append no audit events");
                    change.CommandText = """
                        UPDATE relationship_proposals SET relation_id=$relation,
                            from_id=$from,to_id=$to,kind=$kind,encrypted_label=$label,
                            submitter=$submitter,identity_envelope=$identity
                        WHERE family_id=$f AND id=$target;
                        """;
                    change.Parameters.AddWithValue("$relation", relationId);
                    change.Parameters.AddWithValue("$from", fromId);
                    change.Parameters.AddWithValue("$to", toId);
                    change.Parameters.AddWithValue("$kind", kind);
                    change.Parameters.AddWithValue("$label", label);
                    change.Parameters.AddWithValue("$submitter", submitter);
                    change.Parameters.AddWithValue("$identity", identity);
                    Check(change.ExecuteNonQuery() == 1,
                        "restore original pending proposal envelope");
                }
                store.ReviewRelationship(family, admin, identityTarget, false);
                store.ReviewRelationship(family, admin, identitySource, false);
                var pendingProposals = new List<Guid>();
                for (var index = 0; index < 105; index++)
                    pendingProposals.Add(store.ProposeRelationship(family, actor,
                        Relationship.Create(personA.Id, personB.Id,
                            RelationKind.Neighbor, $"fictional-pending-page-{index}")));
                var malformedIdProposal = pendingProposals.Min();
                var malformedSubmitterProposal = pendingProposals
                    .Where(x => x != malformedIdProposal).Min();
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = database, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var change = connection.CreateCommand();
                    change.CommandText = """
                        UPDATE relationship_proposals SET id='!invalid-guid'
                        WHERE family_id=$f AND id=$badId;
                        """;
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$badId",
                        malformedIdProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "tamper pending proposal ID format");
                    change.CommandText = """
                        UPDATE relationship_proposals SET submitter='not-a-guid'
                        WHERE family_id=$f AND id=$badSubmitter;
                        """;
                    change.Parameters.Clear();
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$badSubmitter",
                        malformedSubmitterProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "tamper pending submitter format");
                    var firstPage = store.ListPendingRelationships(family, admin);
                    Check(firstPage.Count == 100 &&
                        firstPage.Any(x => x.ProposalId == Guid.Empty &&
                            x.SubmittedBy == actor && x.Relationship is null &&
                            x.ContentUnreadable) &&
                        firstPage.Any(x => x.ProposalId == malformedSubmitterProposal &&
                            x.SubmittedBy == Guid.Empty && x.Relationship is null &&
                            x.ContentUnreadable),
                        "malformed pending identities are quarantined without hiding healthy items");
                    change.CommandText = """
                        UPDATE relationship_proposals SET id=$id
                        WHERE family_id=$f AND id='!invalid-guid';
                        """;
                    change.Parameters.Clear();
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$id",
                        malformedIdProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "restore pending proposal ID");
                    change.CommandText = """
                        UPDATE relationship_proposals SET submitter=$submitter
                        WHERE family_id=$f AND id=$id;
                        """;
                    change.Parameters.Clear();
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$submitter", actor.ToString("D"));
                    change.Parameters.AddWithValue("$id",
                        malformedSubmitterProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "restore pending submitter ID");
                    var secondPage = store.ListPendingRelationships(
                        family, admin, firstPage[^1].ProposalId);
                    var discoveredIds = firstPage.Concat(secondPage)
                        .Select(x => x.ProposalId).ToArray();
                    Check(secondPage.Count == 5 && discoveredIds.Length == 105 &&
                        discoveredIds.Distinct().Count() == 105 &&
                        discoveredIds.Contains(Guid.Empty) &&
                        pendingProposals.Where(x => x != malformedIdProposal)
                            .All(discoveredIds.Contains),
                        "pending cursor discovers every item beyond the first bounded page");
                }
                foreach (var proposal in pendingProposals)
                    store.ReviewRelationship(family, admin, proposal, false);
                Expect<ArgumentException>(() => store.ListPendingRelationships(
                    family, admin, Guid.Empty), "empty pending cursor is invalid");
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
                var graphBeforeTamper = store.ReadRelationships(family, admin);
                using (var tamperProposal = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = database, Pooling = false }.ToString()))
                {
                    tamperProposal.Open();
                    string originalRelationId, originalFromId, originalToId;
                    string originalKind, originalSubmitter;
                    byte[] originalLabel, originalIdentity;
                    using (var original = tamperProposal.CreateCommand())
                    {
                        original.CommandText = """
                            SELECT relation_id,from_id,to_id,kind,encrypted_label,
                                   submitter,identity_envelope
                            FROM relationship_proposals WHERE family_id=$f AND id=$id;
                            """;
                        original.Parameters.AddWithValue("$f", family.ToString("D"));
                        original.Parameters.AddWithValue("$id", originalProposal.ToString("D"));
                        using var reader = original.ExecuteReader();
                        Check(reader.Read(), "read the original proposal envelope");
                        originalRelationId = reader.GetString(0);
                        originalFromId = reader.GetString(1);
                        originalToId = reader.GetString(2);
                        originalKind = reader.GetString(3);
                        originalLabel = reader.GetFieldValue<byte[]>(4);
                        originalSubmitter = reader.GetString(5);
                        originalIdentity = reader.GetFieldValue<byte[]>(6);
                    }
                    using var change = tamperProposal.CreateCommand();
                    change.CommandText = """
                        UPDATE relationship_proposals SET
                            relation_id=(SELECT relation_id FROM relationship_proposals
                                         WHERE family_id=$f AND id=$other),
                            from_id=(SELECT from_id FROM relationship_proposals
                                     WHERE family_id=$f AND id=$other),
                            to_id=(SELECT to_id FROM relationship_proposals
                                   WHERE family_id=$f AND id=$other),
                            kind=(SELECT kind FROM relationship_proposals
                                  WHERE family_id=$f AND id=$other),
                            encrypted_label=(SELECT encrypted_label
                                             FROM relationship_proposals
                                             WHERE family_id=$f AND id=$other),
                            submitter=(SELECT submitter FROM relationship_proposals
                                       WHERE family_id=$f AND id=$other),
                            identity_envelope=(SELECT identity_envelope
                                               FROM relationship_proposals
                                               WHERE family_id=$f AND id=$other)
                        WHERE family_id=$f AND id=$id;
                        """;
                    change.Parameters.AddWithValue("$other", otherProposal.ToString("D"));
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$id", originalProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1,
                        "substitute another proposal's complete metadata and encrypted envelopes");
                    Expect<InvalidDataException>(() => store.UndoRelationship(
                        family, admin, originalProposal),
                        "full-envelope substitution cannot undo another proposal's link");
                    Check(store.ReadRelationships(family, admin).SequenceEqual(graphBeforeTamper) &&
                          store.CountAudit(family, admin) == auditBeforeTamper,
                        "full-envelope substitution leaves both links and audit unchanged");
                    change.CommandText = """
                        UPDATE relationship_proposals SET relation_id=$relation,
                            from_id=$from,to_id=$to,kind=$kind,encrypted_label=$label,
                            submitter=$submitter,identity_envelope=$identity
                        WHERE family_id=$f AND id=$id;
                        """;
                    change.Parameters.AddWithValue("$relation", originalRelationId);
                    change.Parameters.AddWithValue("$from", originalFromId);
                    change.Parameters.AddWithValue("$to", originalToId);
                    change.Parameters.AddWithValue("$kind", originalKind);
                    change.Parameters.AddWithValue("$label", originalLabel);
                    change.Parameters.AddWithValue("$submitter", originalSubmitter);
                    change.Parameters.AddWithValue("$identity", originalIdentity);
                    Check(change.ExecuteNonQuery() == 1,
                        "restore original authenticated proposal envelope");
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
                Check(store.ReadRelationships(family, admin).Count==2,
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
