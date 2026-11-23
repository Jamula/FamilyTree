using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FamilyTree.Domain;
using FamilyTree.Persistence;
using Microsoft.Data.Sqlite;

internal static class SqliteFamilyStoreAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var directory = Path.Join(Path.GetTempPath(),
            "familytree-synthetic-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Join(directory, "fictional-family.sqlite");
        var secret = RandomNumberGenerator.GetBytes(32);
        var wrongSecret = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var otherFamily = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var otherAdmin = Guid.NewGuid();
        var member = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var fictionalPerson = new Person(Guid.NewGuid(), "Fictional River Person");
        var pendingCipherProposal = Guid.Empty;
        try
        {
            Expect<ArgumentException>(() => new SqliteFamilyStore("relative.db", secret),
                "absolute path");
            Expect<ArgumentException>(() => new SqliteFamilyStore(path, new byte[8]),
                "invalid key");
            Expect<ArgumentNullException>(() => new SqliteFamilyStore(path, null!),
                "missing key");
            using (var store = new SqliteFamilyStore(path, secret))
            {
                store.CreateFictionalFamily(family, admin);
                store.CreateFictionalFamily(otherFamily, otherAdmin);
                Expect<SqliteException>(() => store.CreateFictionalFamily(family, admin),
                    "bootstrap cannot duplicate an existing family");
                Expect<UnauthorizedAccessException>(() => store.ReadPeople(family, unknown),
                    "unjoined person cannot read records");
                Expect<UnauthorizedAccessException>(() => store.ReadPeople(otherFamily, admin),
                    "cross-family reads denied");
                Expect<UnauthorizedAccessException>(() => store.IssueInvitation(
                    family, unknown, member, DateTimeOffset.UtcNow.AddHours(1),
                    DateTimeOffset.UtcNow), "unauthorized invitation issuer");
                Expect<ArgumentException>(() => store.IssueInvitation(
                    family, admin, admin, DateTimeOffset.UtcNow.AddHours(1),
                    DateTimeOffset.UtcNow), "self invitation");
                Expect<ArgumentException>(() => store.IssueInvitation(
                    family, admin, member, DateTimeOffset.UtcNow.AddMinutes(-1),
                    DateTimeOffset.UtcNow), "expired issued invitation");
                var expiredRecipient = Guid.NewGuid();
                var expired = store.IssueInvitation(family, admin, expiredRecipient,
                    DateTimeOffset.UtcNow.AddSeconds(2), DateTimeOffset.UtcNow);
                Expect<UnauthorizedAccessException>(() => store.AcceptInvitation(
                    family, expiredRecipient, expired, DateTimeOffset.UtcNow.AddHours(1)),
                    "expired or wrong recipient invitation");
                var invitation = store.IssueInvitation(family, admin, member,
                    DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
                Expect<UnauthorizedAccessException>(() => store.AcceptInvitation(
                    otherFamily, member, invitation, DateTimeOffset.UtcNow),
                    "cross-family invitation acceptance");
                Expect<UnauthorizedAccessException>(() => store.AcceptInvitation(
                    family, unknown, invitation, DateTimeOffset.UtcNow),
                    "wrong invited principal");
                store.AcceptInvitation(family, member, invitation, DateTimeOffset.UtcNow);
                Expect<UnauthorizedAccessException>(() => store.AcceptInvitation(
                    family, member, invitation, DateTimeOffset.UtcNow),
                    "accepted invite cannot replay");
                Expect<UnauthorizedAccessException>(() => store.ReadPeople(family, member),
                    "pending membership not active");
                Expect<UnauthorizedAccessException>(() => store.ApproveMember(
                    family, member, member), "cannot self approve membership");
                Expect<UnauthorizedAccessException>(() => store.ApproveMember(
                    family, unknown, member), "non-admin cannot approve membership");
                Expect<UnauthorizedAccessException>(() => store.ApproveMember(
                    otherFamily, admin, member), "cross-family moderation denied");
                store.ApproveMember(family, admin, member);
                Expect<UnauthorizedAccessException>(() => store.ApproveMember(
                    family, admin, member), "membership approved only once");
                var adminProposal = store.ProposePerson(family, admin,
                    new Person(Guid.NewGuid(), "Fictional Self-Review Target"));
                Expect<UnauthorizedAccessException>(() => store.ReviewPerson(
                    family, admin, adminProposal, true),
                    "active administrator cannot approve a self-submitted proposal");
                Check(store.ListPendingPeople(family, admin).Any(x =>
                    x.ProposalId == adminProposal),
                    "pending administrator proposal visible to private review inbox");
                Check(store.ReadPeople(family, member).Count == 0,
                    "approved member sees no unreviewed people");
                Expect<UnauthorizedAccessException>(() => store.ProposePerson(
                    otherFamily, member, fictionalPerson), "cross-family member write");
                Expect<ArgumentException>(() => store.ProposePerson(
                    family, member, new Person(Guid.NewGuid(), "")),
                    "blank name denied");
                Expect<ArgumentException>(() => store.ProposePerson(
                    family, member, new Person(Guid.NewGuid(), "Fictional\nInjected")),
                    "control characters denied");
                var proposal = store.ProposePerson(family, member, fictionalPerson);
                Check(store.ReadPeople(family, admin).Count == 0,
                    "pending proposal hidden from family reads");
                Check(store.ListPendingPeople(family, admin).Any(x =>
                    x.ProposalId == proposal && x.Person == fictionalPerson &&
                    x.SubmittedBy == member),
                    "independent administrator can review exact pending Person diff");
                Expect<UnauthorizedAccessException>(() => store.ListPendingPeople(
                    family, member), "ordinary member cannot enumerate proposals");
                Expect<UnauthorizedAccessException>(() => store.ListPendingPeople(
                    otherFamily, admin), "cross-family pending proposals hidden");
                Expect<InvalidOperationException>(() => store.ProposePerson(
                    family, member, fictionalPerson),
                    "duplicate pending proposal must be rejected");
                Expect<UnauthorizedAccessException>(() => store.ReviewPerson(
                    family, member, proposal, true), "submitter cannot self approve");
                Expect<InvalidOperationException>(() => store.ReviewPerson(
                    otherFamily, otherAdmin, proposal, true),
                    "moderator cannot approve cross-family proposal");
                Expect<InvalidOperationException>(() => store.ReviewPerson(
                    family, admin, Guid.NewGuid(), true),
                    "unknown proposal cannot be approved");
                store.ReviewPerson(family, admin, proposal, true);
                Check(store.ReadPeople(family, member).Single() == fictionalPerson,
                    "approved person visible to authorized member");
                Expect<InvalidOperationException>(() => store.ReviewPerson(
                    family, admin, proposal, true), "cannot replay approved proposal");
                Expect<InvalidOperationException>(() => store.ProposePerson(
                    family, member, fictionalPerson), "duplicate existing person denied");
                var rejected = store.ProposePerson(family, member,
                    new Person(Guid.NewGuid(), "Fictional Rejected"));
                store.ReviewPerson(family, admin, rejected, false);
                Check(store.ReadPeople(family, member).Count == 1,
                    "rejection must not add person");
                Check(store.CountAudit(family, admin) == 3,
                    "membership approval and review decisions audited");
                Expect<UnauthorizedAccessException>(() => store.CountAudit(
                    family, member), "non-admin cannot query audit metadata");
                var secondAdministrator = Guid.NewGuid();
                var secondInvitation = store.IssueInvitation(family, admin,
                    secondAdministrator, DateTimeOffset.UtcNow.AddDays(1),
                    DateTimeOffset.UtcNow);
                store.AcceptInvitation(family, secondAdministrator, secondInvitation,
                    DateTimeOffset.UtcNow);
                store.ApproveMember(family, admin, secondAdministrator);
                Expect<UnauthorizedAccessException>(() => store.PromoteAdministrator(
                    family, secondAdministrator, secondAdministrator),
                    "self-promotion denied");
                store.PromoteAdministrator(family, admin, secondAdministrator);
                Expect<UnauthorizedAccessException>(() => store.PromoteAdministrator(
                    family, admin, secondAdministrator),
                    "repeated promotion denied");
                store.ReviewPerson(family, secondAdministrator, adminProposal, false);
                Check(store.ReadPeople(family, admin).Count == 1,
                    "independent admin rejects original admin proposal");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(
                    fictionalPerson.DisplayName, StringComparison.Ordinal),
                    "person names encrypted at rest");
                pendingCipherProposal = store.ProposePerson(family, member,
                    new Person(Guid.NewGuid(), "Fictional Unapproved Cipher"));
            }
            using (var reopened = new SqliteFamilyStore(path, secret))
            {
                Check(reopened.ReadPeople(family, member).Single() == fictionalPerson,
                    "approved records and membership survive process restart");
                Expect<UnauthorizedAccessException>(() => reopened.ReadPeople(
                    otherFamily, member), "persistent tenant isolation");
                reopened.RevokeMember(family, admin, member);
                Expect<UnauthorizedAccessException>(() => reopened.ReadPeople(
                    family, member), "revocation immediately blocks records");
                Expect<UnauthorizedAccessException>(() => reopened.RevokeMember(
                    family, admin, member), "revocation replay denied");
                Expect<UnauthorizedAccessException>(() => reopened.RevokeMember(
                    family, admin, admin), "cannot revoke last bootstrap administrator");
                Check(reopened.CountAudit(family, admin) == 7, "promotion, review and revocation audited");
                Check(reopened.ReadPeople(family, admin).Count == 1,
                    "administrator retains family scope");
            }
            using (var reopened = new SqliteFamilyStore(path, secret))
                Expect<UnauthorizedAccessException>(() => reopened.ReadPeople(
                    family, member), "revocation persists across restart");
            Expect<InvalidDataException>(() =>
            {
                using var wrong = new SqliteFamilyStore(path, wrongSecret);
            }, "wrong key cannot open or write to an existing family database");
            using (var reopened = new SqliteFamilyStore(path, secret))
                Check(reopened.ReadPeople(family, admin).Single() == fictionalPerson,
                    "wrong-key open attempt cannot poison valid family records");
            using (var direct = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                direct.Open();
                using var change = direct.CreateCommand();
                change.CommandText = "UPDATE person_proposals SET encrypted_name=X'01' WHERE id=$id;";
                change.Parameters.AddWithValue("$id", pendingCipherProposal.ToString("D"));
                Check(change.ExecuteNonQuery() == 1, "pending proposal tampered");
                using (var stillReadable = new SqliteFamilyStore(path, secret))
                {
                    Expect<InvalidDataException>(() => stillReadable.ReviewPerson(
                        family, admin, pendingCipherProposal, true),
                        "tampered pending proposal cannot be promoted into approved data");
                    Check(stillReadable.ReadPeople(family, admin).Single() == fictionalPerson,
                        "malformed pending data cannot poison valid family records");
                }
                change.CommandText = "UPDATE people SET encrypted_name=X'01' WHERE family_id=$f;";
                change.Parameters.AddWithValue("$f", family.ToString("D"));
                Check(change.ExecuteNonQuery() == 1, "tampering test modified only one person");
            }
            using (var corrupted = new SqliteFamilyStore(path, secret))
            {
                Expect<InvalidDataException>(() => corrupted.ReadPeople(family, admin),
                    "damaged ciphertext is rejected without exposing content");
                corrupted.Dispose();
                Expect<ObjectDisposedException>(() => corrupted.ReadPeople(family, admin),
                    "disposed store cannot access encryption key");
            }
            Console.WriteLine("PASS SQLite synthetic durable membership, approval, encrypted names, restart, denial and tamper tests");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(wrongSecret);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void Check(bool success, string reason)
    {
        if (!success) throw new Exception(reason);
    }
    private static void Expect<T>(Action action, string reason) where T: Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception(reason);
    }
}
