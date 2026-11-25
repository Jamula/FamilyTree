using System.Security.Cryptography;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;
using Permission = FamilyTree.Persistence.SqliteFamilyStore.ScopedPersonPermission;

internal static class SqliteScopedPersonRaceAssertions
{
    internal static async Task RunAsync()
    {
        var directory = Path.Join(Path.GetTempPath(), "scoped-person-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "synthetic.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var author = Guid.NewGuid();
        var reviewer = Guid.NewGuid();
        try
        {
            using var first = new SqliteFamilyStore(path, key);
            first.CreateFictionalFamily(family, admin);
            foreach (var member in new[] { author, reviewer })
            {
                var now = DateTimeOffset.UtcNow;
                var invite = first.IssueInvitation(family, admin, member, now.AddHours(1), now);
                first.AcceptInvitation(family, member, invite, now);
                first.ApproveMember(family, admin, member);
            }
            first.GrantScopedPersonPermission(family, branch, admin, author, Permission.CreatePerson);
            first.GrantScopedPersonPermission(family, branch, admin, reviewer, Permission.ReviewPerson);
            var proposal = first.ProposeScopedPerson(family, branch, author, "Synthetic Race");
            var person = first.ListScopedPendingPeople(family, branch, reviewer).Single().Person;
            using var second = new SqliteFamilyStore(path, key);
            using var gate = new ManualResetEventSlim(false);
            var review = Task.Run(() =>
            {
                gate.Wait();
                try { second.ReviewScopedPerson(family, branch, reviewer, proposal, true); return true; }
                catch (UnauthorizedAccessException) { return false; }
            });
            var revoke = Task.Run(() =>
            {
                gate.Wait();
                first.RevokeMember(family, admin, author);
            });
            gate.Set();
            await Task.WhenAll(review, revoke);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Pooling = false }.ToString());
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM people WHERE family_id=$f AND id=$p;";
            cmd.Parameters.AddWithValue("$f", family.ToString("D"));
            cmd.Parameters.AddWithValue("$p", person.Id.ToString("D"));
            if (((long)cmd.ExecuteScalar()! == 1) != review.Result)
                throw new Exception("race result disagrees with durable publication");
            var denied = false;
            try
            {
                first.ProposeScopedPerson(family, branch, author, "Synthetic Denied");
            }
            catch (UnauthorizedAccessException) { denied = true; }
            if (!denied) throw new Exception("revoked author retained grant");
            Console.WriteLine("PASS SQLite scoped review versus revocation race");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(directory, recursive: true);
        }
    }
}
