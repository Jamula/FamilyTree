using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

/// <summary>
/// Keyset pagination and durable undo discovery with more approvals than
/// one bounded page. Uses only synthetic people and relationships.
/// </summary>
internal static class SqliteApprovedPaginationAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "familytree-undo-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Join(directory, "synthetic.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var member = Guid.NewGuid();
        var first = new Person(Guid.NewGuid(), "Fictional Page A");
        var second = new Person(Guid.NewGuid(), "Fictional Page B");
        var proposals = new HashSet<Guid>();
        try
        {
            using (var setup = new SqliteFamilyStore(database, key))
            {
                setup.CreateFictionalFamily(family, admin);
                var invite = setup.IssueInvitation(family, admin, member,
                    DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
                setup.AcceptInvitation(family, member, invite, DateTimeOffset.UtcNow);
                setup.ApproveMember(family, admin, member);
                foreach (var person in new[] { first, second })
                    setup.ReviewPerson(family, admin,
                        setup.ProposePerson(family, member, person), true);
                for (var i = 0; i < 105; i++)
                {
                    var relation = Relationship.Create(first.Id, second.Id,
                        RelationKind.Neighbor, "Fictional page " + i);
                    var proposal = setup.ProposeRelationship(family, member, relation);
                    setup.ReviewRelationship(family, admin, proposal, true);
                    Check(proposals.Add(proposal), "proposal IDs must be distinct");
                }
                Check(setup.ReadRelationships(family, admin).Count == 105,
                    "all approved synthetic links remain distinct");
            }
            using (var reopened = new SqliteFamilyStore(database, key))
            {
                var orderedProposals = proposals.OrderBy(x => x.ToString("D"),
                    StringComparer.Ordinal).ToArray();
                var malformedIdProposal = orderedProposals[99];
                var malformedSubmitterProposal = proposals
                    .Where(x => x != malformedIdProposal).Min();
                var malformedId = malformedIdProposal.ToString("D") + "!";
                using (var connection = new SqliteConnection(
                    new SqliteConnectionStringBuilder {
                        DataSource = database, Pooling = false }.ToString()))
                {
                    connection.Open();
                    using var change = connection.CreateCommand();
                    change.CommandText = """
                        UPDATE relationship_proposals SET id=$malformedId
                        WHERE family_id=$f AND id=$badId;
                        """;
                    change.Parameters.Clear();
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$malformedId", malformedId);
                    change.Parameters.AddWithValue("$badId",
                        malformedIdProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "tamper approved proposal ID format");
                    change.CommandText = """
                        UPDATE relationship_proposals SET submitter='not-a-guid'
                        WHERE family_id=$f AND id=$badSubmitter;
                        """;
                    change.Parameters.Clear();
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$badSubmitter",
                        malformedSubmitterProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "tamper approved submitter format");
                    var quarantined = reopened.ListApprovedRelationshipsForUndo(
                        family, admin);
                    Check(quarantined.Count == 100 &&
                        quarantined[^1].ProposalId == Guid.Empty &&
                        quarantined[^1].Cursor == malformedId &&
                        quarantined.Any(x => x.ProposalId == Guid.Empty &&
                            x.SubmittedBy == member && x.Relationship is null &&
                            x.ContentUnreadable) &&
                        quarantined.Any(x => x.ProposalId == malformedSubmitterProposal &&
                            x.SubmittedBy == Guid.Empty && x.Relationship is null &&
                            x.ContentUnreadable),
                        "malformed approved identities are quarantined without hiding healthy items");
                    var afterMalformedBoundary = reopened.ListApprovedRelationshipsForUndo(
                        family, admin, quarantined[^1].Cursor);
                    Check(afterMalformedBoundary.Count == 5 &&
                        afterMalformedBoundary.All(x => x.Relationship is not null),
                        "raw approved cursor reaches healthy proposals after malformed boundary");
                    change.CommandText = """
                        UPDATE relationship_proposals SET id=$id
                        WHERE family_id=$f AND id=$malformedId;
                        """;
                    change.Parameters.Clear();
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$malformedId", malformedId);
                    change.Parameters.AddWithValue("$id",
                        malformedIdProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "restore approved proposal ID");
                    change.CommandText = """
                        UPDATE relationship_proposals SET submitter=$submitter
                        WHERE family_id=$f AND id=$id;
                        """;
                    change.Parameters.Clear();
                    change.Parameters.AddWithValue("$f", family.ToString("D"));
                    change.Parameters.AddWithValue("$submitter", member.ToString("D"));
                    change.Parameters.AddWithValue("$id",
                        malformedSubmitterProposal.ToString("D"));
                    Check(change.ExecuteNonQuery() == 1, "restore approved submitter ID");
                }
                var page1 = reopened.ListApprovedRelationshipsForUndo(family, admin);
                Check(page1.Count == 100, "first page is bounded to 100 approvals");
                var page2 = reopened.ListApprovedRelationshipsForUndo(
                    family, admin, page1[^1].Cursor);
                Check(page2.Count == 5, "cursor retrieves remaining five approvals");
                Check(reopened.ListApprovedRelationshipsForUndo(
                    family, admin, page2[^1].Cursor).Count == 0,
                    "terminal page is empty");
                var found = page1.Concat(page2).Select(x => x.ProposalId).ToHashSet();
                Check(found.SetEquals(proposals) &&
                    page1.All(x => x.Relationship is not null) &&
                    page2.All(x => x.Relationship is not null),
                    "all 105 approved proposals rediscovered after restart");
                Check(reopened.CountAudit(family, admin) == 108,
                    "pagination does not mutate approved relationships or audit");
                Expect<ArgumentException>(() => reopened.ListApprovedRelationshipsForUndo(
                    family, admin, ""), "empty cursor is invalid");
                Expect<UnauthorizedAccessException>(() =>
                    reopened.ListApprovedRelationshipsForUndo(family, member),
                    "ordinary member cannot read approved undo history");
                Expect<UnauthorizedAccessException>(() =>
                    reopened.ListApprovedRelationshipsForUndo(Guid.NewGuid(), admin),
                    "other-family caller cannot read approved undo history");
                reopened.UndoRelationship(family, admin, page2[^1].ProposalId);
                Check(reopened.ReadRelationships(family, admin).Count == 104,
                    "cursor-selected approval can be reversed without touching other links");
                Check(reopened.CountAudit(family, admin) == 109,
                    "cursor-targeted undo creates only one audit event");
            }
            using (var afterRestart = new SqliteFamilyStore(database, key))
            {
                Check(afterRestart.ReadRelationships(family, member).Count == 104,
                    "approved queue and undo survive second restart");
                Check(afterRestart.ListApprovedRelationshipsForUndo(family, admin)
                    .Count == 100,
                    "pagination stays bounded after an undo without deleting others");
            }
            Console.WriteLine("PASS durable 100+ approved undo pagination, cursor, privacy and restart");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
    private static void Expect<T>(Action action, string message) where T : Exception
    {
        var matched = false;
        try { action(); } catch (T) { matched = true; }
        if (!matched) throw new InvalidOperationException(message);
    }
}
