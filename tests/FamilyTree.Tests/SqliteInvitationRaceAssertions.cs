using System.Security.Cryptography;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

internal static class SqliteInvitationRaceAssertions
{
    internal static async Task RunAsync()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "familytree-invitation-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "synthetic.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var member = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        try
        {
            Guid first, second;
            using (var setup = new SqliteFamilyStore(path, key))
            {
                setup.CreateFictionalFamily(family, admin);
                first = setup.IssueInvitation(family, admin, member, now.AddHours(1), now);
                second = setup.IssueInvitation(family, admin, member, now.AddHours(1), now);
            }

            using var ready = new CountdownEvent(2);
            using var attempting = new CountdownEvent(2);
            using var start = new ManualResetEventSlim();
            Task<bool> Accept(Guid invitation) => Task.Factory.StartNew(() =>
            {
                using var store = new SqliteFamilyStore(path, key);
                ready.Signal();
                if (!start.Wait(TimeSpan.FromSeconds(30)))
                    throw new InvalidOperationException("Invitation race did not start.");
                attempting.Signal();
                try
                {
                    store.AcceptInvitation(family, member, invitation, now);
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            var attempts = new[] { Accept(first), Accept(second) };
            bool bothReady;
            bool bothAttempting = false;
            bool neitherCompleted = false;
            try
            {
                bothReady = ready.Wait(TimeSpan.FromSeconds(30));
                if (bothReady)
                {
                    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false
                    }.ToString());
                    connection.Open();
                    using var transaction = connection.BeginTransaction();
                    start.Set();
                    bothAttempting = attempting.Wait(TimeSpan.FromSeconds(4));
                    neitherCompleted = attempts.All(x => !x.IsCompleted);
                    transaction.Rollback();
                }
            }
            finally
            {
                start.Set();
            }
            var outcomes = await Task.WhenAll(attempts);
            Check(bothReady, "Invitation racers did not become ready.");
            Check(bothAttempting && neitherCompleted, "Both attempts must start while the database write lock is held.");
            Check(outcomes.Count(x => x) == 1, "Exactly one invitation may create pending membership.");
            var winner = outcomes[0] ? first : second;
            var loser = outcomes[0] ? second : first;
            Check(InvitationState(path, winner) == "Accepted" &&
                InvitationState(path, loser) == "Offered",
                "The losing invitation update must roll back.");

            using (var reopened = new SqliteFamilyStore(path, key))
            {
                ExpectDenied(() => reopened.ReadPeople(family, member), "Pending membership cannot read.");
                ExpectDenied(() => reopened.ApproveMember(family, member, member), "Self approval denied.");
                ExpectDenied(() => reopened.AcceptInvitation(family, member, first, now), "First invitation cannot replay.");
                ExpectDenied(() => reopened.AcceptInvitation(family, member, second, now), "Second invitation cannot bypass pending membership.");
                reopened.ApproveMember(family, admin, member);
                Check(reopened.ReadPeople(family, member).Count == 0, "Independent approval activates membership.");
                Check(reopened.CountAudit(family, admin) == 1, "Only one approval is audited.");
                reopened.RevokeMember(family, admin, member);
            }
            using (var reopened = new SqliteFamilyStore(path, key))
            {
                ExpectDenied(() => reopened.ReadPeople(family, member), "Revocation remains effective after reopening.");
                ExpectDenied(() => reopened.AcceptInvitation(family, member, first, now), "Old invitation cannot reactivate membership.");
                ExpectDenied(() => reopened.AcceptInvitation(family, member, second, now), "Rolled-back offer cannot reactivate membership.");
                Check(InvitationState(path, loser) == "Revoked", "Revocation invalidates the rolled-back offer.");
                Check(reopened.CountAudit(family, admin) == 2, "Approval and revocation are each audited once.");
            }
            Console.WriteLine("PASS synthetic SQLite invitation race, rollback, approval and durable revocation");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string? InvitationState(string path, Guid invitation)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state FROM invitations WHERE id=$id;";
        command.Parameters.AddWithValue("$id", invitation.ToString("D"));
        return command.ExecuteScalar() as string;
    }

    private static void ExpectDenied(Action action, string message)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(message);
    }
}
