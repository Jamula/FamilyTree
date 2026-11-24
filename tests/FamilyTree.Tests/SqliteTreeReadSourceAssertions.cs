using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyTree.Domain;
using FamilyTree.Persistence;

internal static class SqliteTreeReadSourceAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var folder = Path.Join(Path.GetTempPath(), "familytree-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, "fictional.sqlite");
        var key = RandomNumberGenerator.GetBytes(32);
        var family = Guid.NewGuid();
        var otherFamily = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var member = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var person = new Person(Guid.NewGuid(), "Fictional Read Person");
        var hidden = new Person(Guid.NewGuid(), "Fictional Hidden Person");
        try
        {
            using var store = new SqliteFamilyStore(path, key);
            store.CreateFictionalFamily(family, admin);
            store.CreateFictionalFamily(otherFamily, Guid.NewGuid());
            Invite(store, family, admin, member, true);
            Invite(store, family, admin, pending, false);
            var proposal = store.ProposePerson(family, member, person);
            store.ReviewPerson(family, admin, proposal, true);
            proposal = store.ProposePerson(family, member, hidden);
            store.ReviewPerson(family, admin, proposal, true);
            var grants = new SqliteGraphGrantStore(path);
            using var source = new SqliteTreeReadSource(path, key);
            var service = new PrivateTreeReadService(source);
            AuthorizedTreeProjection? Read(Guid viewer, Guid tenant, Guid scope, Guid center) =>
                service.GetAsync(viewer, tenant, scope, center, new TreeOptions()).GetAwaiter().GetResult();

            Check(Read(member, family, branch, person.Id) is null, "membership alone denied");
            Expect<UnauthorizedAccessException>(() => grants.GrantDisplayName(
                family, branch, admin, member, person.Id), "field grant requires person grant");
            grants.GrantPerson(family, branch, admin, member, person.Id);
            grants.GrantPerson(family, branch, admin, member, hidden.Id);
            Check(Read(member, family, branch, person.Id) is null, "person grant alone denied");
            Expect<UnauthorizedAccessException>(() => grants.GrantDisplayName(
                family, branch, member, member, person.Id), "non-admin grant denied");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            var result = Read(member, family, branch, person.Id);
            Check(result is not null && result.Nodes.Count == 1 &&
                result.Nodes[0].DisplayName == person.DisplayName && result.Edges.Count == 0,
                "explicit grants allow only isolated redacted person");
            Check(Read(member, family, branch, hidden.Id) is null &&
                Read(member, family, branch, Guid.NewGuid()) is null,
                "hidden and unknown centers indistinguishable");
            Check(Read(Guid.Empty, family, branch, person.Id) is null &&
                Read(Guid.NewGuid(), family, branch, person.Id) is null &&
                Read(pending, family, branch, person.Id) is null &&
                Read(member, otherFamily, branch, person.Id) is null &&
                Read(member, family, Guid.NewGuid(), person.Id) is null,
                "anonymous, forged, pending and cross-scope principals denied");
            using (var reopened = new SqliteTreeReadSource(path, key))
                Check(new PrivateTreeReadService(reopened).GetAsync(member, family, branch,
                    person.Id, new TreeOptions()).GetAwaiter().GetResult() is not null,
                    "grants survive reopening");
            grants.RevokeDisplayName(family, branch, admin, member, person.Id);
            grants.RevokeDisplayName(family, branch, admin, member, person.Id);
            Check(Read(member, family, branch, person.Id) is null, "field revocation denies read");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            grants.RevokePerson(family, branch, admin, member, person.Id);
            grants.GrantPerson(family, branch, admin, member, person.Id);
            Check(Read(member, family, branch, person.Id) is null,
                "person revocation removes field grant permanently");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            store.RevokeMember(family, admin, member);
            Check(Read(member, family, branch, person.Id) is null, "revoked member denied immediately");
            Invite(store, family, admin, member, true);
            grants.GrantPerson(family, branch, admin, member, person.Id);
            Check(Read(member, family, branch, person.Id) is null,
                "rejoined member does not inherit field permissions");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Expect<OperationCanceledException>(() => source.LoadAsync(member, family, branch,
                cancelled.Token).GetAwaiter().GetResult(), "cancelled source denied");
            Check(source.LoadAsync(Guid.Empty, family, branch, default).GetAwaiter().GetResult() is null,
                "empty source identity denied");
            Console.WriteLine("PASS persisted tree read grants, isolation and revocation");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(folder, true);
        }
    }

    private static void Invite(SqliteFamilyStore store, Guid family, Guid admin, Guid member, bool approve)
    {
        var now = DateTimeOffset.UtcNow;
        var invitation = store.IssueInvitation(family, admin, member, now.AddDays(1), now);
        store.AcceptInvitation(family, member, invitation, now);
        if (approve) store.ApproveMember(family, admin, member);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
