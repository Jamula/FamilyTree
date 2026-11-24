using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Globalization;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

/// <summary>
/// Real two-connection SQLite race tests, kept OUT of module initialization
/// so thread pool continuations cannot block on CLR startup locks.
/// </summary>
internal static class SqliteRelationshipConcurrencyAssertions
{
    internal static async Task RunAsync()
    {
        var folder = Path.Join(Path.GetTempPath(),
            "familytree-races-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, "synthetic.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var moderator = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var first = new Person(Guid.NewGuid(), "Fictional A");
        var second = new Person(Guid.NewGuid(), "Fictional B");

        try
        {
            using (var setup = new SqliteFamilyStore(path, key))
            {
                setup.CreateFictionalFamily(family, admin);
                foreach (var invitee in new[] {actor, moderator})
                {
                    var invitation = setup.IssueInvitation(family, admin, invitee,
                        DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
                    setup.AcceptInvitation(family, invitee, invitation, DateTimeOffset.UtcNow);
                    setup.ApproveMember(family, admin, invitee);
                }
                setup.PromoteAdministrator(family, admin, moderator);
                foreach (var person in new[] {first, second})
                    setup.ReviewPerson(family, admin,
                        setup.ProposePerson(family, actor, person), true);
            }

            using var store1 = new SqliteFamilyStore(path, key);
            using var store2 = new SqliteFamilyStore(path, key);
            var auditBeforeDuplicates = ReadAudit(path, family);

            var duplicate = Relationship.Create(first.Id, second.Id, RelationKind.Friend,
                "Fictional concurrent duplicate");
            var proposals = await Race(
                () => store1.ProposeRelationship(family, actor, duplicate),
                () => store2.ProposeRelationship(family, actor, duplicate));
            Check(proposals.Count(x => x.Error is null) == 1,
                "concurrent duplicate submissions have exactly one winner");
            Check(proposals.Count(x => x.Error is not null && ExpectedConflict(x.Error)) == 1,
                "losing duplicate submission rejected without corrupting storage");
            var winner = proposals.Single(x => x.Error is null).Value;
            Check(winner != Guid.Empty, "winning proposal has valid ID");
            Check(auditBeforeDuplicates.SequenceEqual(ReadAudit(path, family)),
                "competing duplicate submissions add no review audit events");

            using (var reopened = new SqliteFamilyStore(path, key))
            {
                Check(reopened.ListPendingRelationships(family, admin).Count == 1,
                    "reopened store exposes exactly one pending duplicate");
                var auditBefore = ReadAudit(path, family);
                var reviews = await Race(
                    () => {store1.ReviewRelationship(family, admin, winner, true); return true;},
                    () => {store2.ReviewRelationship(family, moderator, winner, true); return true;});
                Check(reviews.Count(x => x.Error is null) == 1,
                    "competing moderators can approve a proposal exactly once");
                Check(reviews.Count(x => x.Error is not null && ExpectedConflict(x.Error)) == 1,
                    "losing review rejected or retried without double publishing");
                var reviewActor = reviews[0].Error is null ? admin : moderator;
                using var checkedAgain = new SqliteFamilyStore(path, key);
                Check(checkedAgain.ReadRelationships(family, actor).Count == 1 &&
                      VerifyAuditDelta(auditBefore, ReadAudit(path, family),
                          (winner, reviewActor, "relationship-approved")),
                    "competing approval persists one exact actor/subject/action audit");
            }

            var racedEdge = Relationship.Create(first.Id, second.Id, RelationKind.Neighbor,
                "Fictional review-revoke race");
            var raceProposal = store1.ProposeRelationship(family, actor, racedEdge);
            var auditPreRevoke = ReadAudit(path, family);
            var race = await Race(
                () => {store2.ReviewRelationship(family, moderator, raceProposal, true);
                    return true;},
                () => {store1.RevokeMember(family, admin, moderator);
                    return true;});
            Check(race[1].Error is null, "administrator revocation must commit");
            Check(race[0].Error is null ||
                  race[0].Error is UnauthorizedAccessException ||
                  ExpectedConflict(race[0].Error),
                "revoked moderator cannot silently approve");
            var approvedRace = race[0].Error is null;
            using (var reopened = new SqliteFamilyStore(path, key))
            {
                var expectedEvents = approvedRace
                    ? new[]
                    {
                        (raceProposal, moderator, "relationship-approved"),
                        (moderator, admin, "member-revoked")
                    }
                    : new[] {(moderator, admin, "member-revoked")};
                Check(VerifyAuditDelta(auditPreRevoke, ReadAudit(path, family),
                        expectedEvents),
                    "review/revocation audit has exact ordered committed transitions only");
                Check(reopened.ReadRelationships(family, actor)
                    .Any(x => x.Id == racedEdge.Id) == approvedRace,
                    "reopened graph agrees with committed review outcome");
                Expect<UnauthorizedAccessException>(
                    () => reopened.ReadRelationships(family, moderator),
                    "revoked moderator loses read access immediately");
                Expect<UnauthorizedAccessException>(
                    () => reopened.ReviewRelationship(family, moderator, raceProposal, true),
                    "revoked moderator cannot replay racing approval");
                Check(VerifyAuditDelta(auditPreRevoke, ReadAudit(path, family),
                        expectedEvents),
                    "denied review replay adds no persisted audit event");
            }

            // Race undo against revocation of a second independent moderator.
            // Either transaction may win, but both the graph and the audit
            // must reproduce that exact order after a fresh database reopen.
            var undoModerator = Guid.NewGuid();
            var undoInvite = store1.IssueInvitation(family, admin, undoModerator,
                DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
            store1.AcceptInvitation(family, undoModerator, undoInvite, DateTimeOffset.UtcNow);
            store1.ApproveMember(family, admin, undoModerator);
            store1.PromoteAdministrator(family, admin, undoModerator);
            var auditBeforeUndoRace = ReadAudit(path, family);
            var undoRace = await Race(
                () => {store2.UndoRelationship(family, undoModerator, winner);
                    return true;},
                () => {store1.RevokeMember(family, admin, undoModerator);
                    return true;});
            Check(undoRace[1].Error is null,
                "administrator revocation commits during undo race");
            Check(undoRace[0].Error is null ||
                  undoRace[0].Error is UnauthorizedAccessException ||
                  ExpectedConflict(undoRace[0].Error),
                "undo cannot succeed after administrator revocation wins");
            var undoWon = undoRace[0].Error is null;
            using (var reopened = new SqliteFamilyStore(path, key))
            {
                Check(reopened.ReadRelationships(family, actor)
                    .Any(x => x.Id == duplicate.Id) != undoWon,
                    "reopened relation state matches the actual undo race winner");
                var expectedRaceEvents = undoWon
                    ? new[]
                    {
                        (winner, undoModerator, "relationship-undone"),
                        (undoModerator, admin, "member-revoked")
                    }
                    : new[] {(undoModerator, admin, "member-revoked")};
                Check(VerifyAuditDelta(auditBeforeUndoRace, ReadAudit(path, family),
                        expectedRaceEvents),
                    "undo/revocation audit has exact ordered committed transitions only");
                Expect<UnauthorizedAccessException>(
                    () => reopened.UndoRelationship(family, undoModerator, winner),
                    "revoked independent moderator can never replay undo");
                Check(VerifyAuditDelta(auditBeforeUndoRace, ReadAudit(path, family),
                        expectedRaceEvents),
                    "denied revoked undo adds no persisted audit event");
                if (!undoWon)
                    reopened.UndoRelationship(family, admin, winner);
                Expect<InvalidOperationException>(
                    () => reopened.UndoRelationship(family, admin, winner),
                    "once reversed, a proposal can never be undone twice");
            }
            using (var afterRestart = new SqliteFamilyStore(path, key))
            {
                Check(!afterRestart.ReadRelationships(family, actor)
                    .Any(x => x.Id == duplicate.Id),
                    "undo survives second restart without resurrecting the relationship");
                var expectedFinalEvents = undoWon
                    ? new[]
                    {
                        (winner, undoModerator, "relationship-undone"),
                        (undoModerator, admin, "member-revoked")
                    }
                    : new[]
                    {
                        (undoModerator, admin, "member-revoked"),
                        (winner, admin, "relationship-undone")
                    };
                Check(VerifyAuditDelta(auditBeforeUndoRace, ReadAudit(path, family),
                        expectedFinalEvents),
                    "reopened audit records exact successful undo and revocation sequence");
            }
            Console.WriteLine(
                "PASS competing SQLite submissions/reviews, moderator revocation and durable undo");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static bool ExpectedConflict(Exception? error) =>
        error is InvalidOperationException ||
        error is SqliteException sql && sql.SqliteErrorCode is 5 or 6;

    private sealed record AuditRow(long Sequence, Guid Subject, Guid Actor,
        string Action, string CreatedUtc);

    private static AuditRow[] ReadAudit(string path, Guid familyId)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder {
                DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq,subject_id,reviewer,action,created_utc
            FROM audit WHERE family_id=$family ORDER BY seq;
            """;
        command.Parameters.AddWithValue("$family", familyId.ToString("D"));
        using var reader = command.ExecuteReader();
        var rows = new List<AuditRow>();
        while (reader.Read())
            rows.Add(new(reader.GetInt64(0), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), reader.GetString(3),
                reader.GetString(4)));
        return rows.ToArray();
    }

    private static bool VerifyAuditDelta(IReadOnlyList<AuditRow> before,
        IReadOnlyList<AuditRow> after,
        params (Guid Subject, Guid Actor, string Action)[] expected)
    {
        if (after.Count != before.Count + expected.Length ||
            !before.SequenceEqual(after.Take(before.Count)))
            return false;
        var lastSequence = before.Count == 0 ? 0 : before[^1].Sequence;
        var added = after.Skip(before.Count).ToArray();
        for (var index = 0; index < added.Length; index++)
        {
            var actual = added[index];
            var wanted = expected[index];
            if (actual.Sequence <= lastSequence ||
                actual.Subject != wanted.Subject || actual.Actor != wanted.Actor ||
                actual.Action != wanted.Action ||
                !DateTimeOffset.TryParse(actual.CreatedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var created) ||
                created.Offset != TimeSpan.Zero)
                return false;
            lastSequence = actual.Sequence;
        }
        return true;
    }

    private sealed record Outcome<T>(T Value, Exception? Error);

    private static async Task<Outcome<T>[]> Race<T>(Func<T> left, Func<T> right)
    {
        using var signal = new Barrier(2);
        Task<Outcome<T>> Run(Func<T> action) => Task.Run(() =>
        {
            if (!signal.SignalAndWait(TimeSpan.FromSeconds(10)))
                return new Outcome<T>(default!, new TimeoutException("Race synchronization"));
            try { return new Outcome<T>(action(), null); }
            // Unexpected errors must FAIL the test rather than being
            // misclassified as an ordinary concurrent transaction conflict.
            catch (InvalidOperationException error)
            { return new Outcome<T>(default!, error); }
            catch (UnauthorizedAccessException error)
            { return new Outcome<T>(default!, error); }
            catch (SqliteException error)
            { return new Outcome<T>(default!, error); }
        });
        return await Task.WhenAll(Run(left), Run(right));
    }

    private static void Check(bool accepted, string reason)
    {
        if (!accepted) throw new InvalidOperationException(reason);
    }
    private static void Expect<T>(Action action, string reason) where T : Exception
    {
        var thrown = false;
        try { action(); } catch (T) { thrown = true; }
        if (!thrown) throw new InvalidOperationException(reason);
    }
}
