using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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

            using (var reopened = new SqliteFamilyStore(path, key))
            {
                Check(reopened.ListPendingRelationships(family, admin).Count == 1,
                    "reopened store exposes exactly one pending duplicate");
                var auditBefore = reopened.CountAudit(family, admin);
                var reviews = await Race(
                    () => {store1.ReviewRelationship(family, admin, winner, true); return true;},
                    () => {store2.ReviewRelationship(family, moderator, winner, true); return true;});
                Check(reviews.Count(x => x.Error is null) == 1,
                    "competing moderators can approve a proposal exactly once");
                Check(reviews.Count(x => x.Error is not null && ExpectedConflict(x.Error)) == 1,
                    "losing review rejected or retried without double publishing");
                using var checkedAgain = new SqliteFamilyStore(path, key);
                Check(checkedAgain.ReadRelationships(family, actor).Count == 1 &&
                      checkedAgain.CountAudit(family, admin) == auditBefore + 1,
                    "competing approval has one relation and precisely one durable audit");
            }

            var racedEdge = Relationship.Create(first.Id, second.Id, RelationKind.Neighbor,
                "Fictional review-revoke race");
            var raceProposal = store1.ProposeRelationship(family, actor, racedEdge);
            var auditPreRevoke = store1.CountAudit(family, admin);
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
                Check(reopened.CountAudit(family, admin) ==
                      auditPreRevoke + 1 + (approvedRace ? 1 : 0),
                    "race audit reflects actual committed review and revocation");
                Check(reopened.ReadRelationships(family, actor)
                    .Any(x => x.Id == racedEdge.Id) == approvedRace,
                    "reopened graph agrees with committed review outcome");
                Expect<UnauthorizedAccessException>(
                    () => reopened.ReadRelationships(family, moderator),
                    "revoked moderator loses read access immediately");
                Expect<UnauthorizedAccessException>(
                    () => reopened.ReviewRelationship(family, moderator, raceProposal, true),
                    "revoked moderator cannot replay racing approval");
            }

            // An independent moderator can still undo after another reviewer
            // loses access, but a revoked actor cannot undo anything.
            using (var reopened = new SqliteFamilyStore(path, key))
            {
                reopened.UndoRelationship(family, admin, winner);
                Check(reopened.CountAudit(family, admin) ==
                      auditPreRevoke + 2 + (approvedRace ? 1 : 0),
                    "independent undo writes exactly one additional durable audit");
                Expect<InvalidOperationException>(
                    () => reopened.UndoRelationship(family, admin, winner),
                    "undo cannot replay");
            }
            using (var afterRestart = new SqliteFamilyStore(path, key))
                Check(!afterRestart.ReadRelationships(family, actor)
                    .Any(x => x.Id == duplicate.Id),
                    "undo survives reopen and does not resurrect duplicate link");
            Console.WriteLine(
                "PASS competing SQLite submissions/reviews, moderator revocation and durable undo");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    private static bool ExpectedConflict(Exception error) =>
        error is InvalidOperationException ||
        error is SqliteException sql && sql.SqliteErrorCode is 5 or 6;

    private sealed record Outcome<T>(T Value, Exception? Error);

    private static async Task<Outcome<T>[]> Race<T>(Func<T> left, Func<T> right)
    {
        using var signal = new Barrier(2);
        Task<Outcome<T>> Run(Func<T> action) => Task.Run(() =>
        {
            if (!signal.SignalAndWait(TimeSpan.FromSeconds(10)))
                return new Outcome<T>(default!, new TimeoutException("Race synchronization"));
            try { return new Outcome<T>(action(), null); }
            catch (Exception error) { return new Outcome<T>(default!, error); }
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
