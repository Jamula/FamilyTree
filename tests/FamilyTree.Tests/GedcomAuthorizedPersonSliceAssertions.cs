using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using FamilyTree.Domain;
using FamilyTree.Persistence;

internal static class GedcomAuthorizedPersonSliceAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var folder = Path.Join(Path.GetTempPath(), "familytree-gedcom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var key = RandomNumberGenerator.GetBytes(32);
        var path = Path.Join(folder, "fictional.sqlite");
        var family = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var member = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var person = new Person(Guid.NewGuid(), "Avery /Fiction/", true);
        var hidden = new Person(Guid.NewGuid(), "Hidden /Fiction/");
        try
        {
            using var store = new SqliteFamilyStore(path, key);
            store.CreateFictionalFamily(family, admin);
            Invite(store, family, admin, member, true);
            Invite(store, family, admin, pending, false);
            foreach (var p in new[] { person, hidden })
                store.ReviewPerson(family, admin, store.ProposePerson(family, member, p), true);
            var edge = Relationship.Create(person.Id, hidden.Id, RelationKind.Friend, "fictional secret link");
            store.ReviewRelationship(family, admin, store.ProposeRelationship(family, member, edge), true);
            var grants = new SqliteGraphGrantStore(path);
            using var source = new SqliteTreeReadSource(path, key);
            var slice = new GedcomAuthorizedPersonSlice(source);
            SyntheticGedcomExport? Export(Guid viewer, Guid f, Guid b, Guid p) =>
                slice.ExportAsync(viewer, f, b, p).GetAwaiter().GetResult();
            SyntheticGedcomPreviewResult? Preview(Guid viewer, Guid f, Guid b, Guid p, byte[]? bytes) =>
                slice.PreviewAsync(viewer, f, b, p, bytes).GetAwaiter().GetResult();
            Check(Export(member, family, branch, person.Id) is null, "membership alone denied");
            grants.GrantPerson(family, branch, admin, member, person.Id);
            grants.GrantPerson(family, branch, admin, member, hidden.Id);
            Check(Export(member, family, branch, person.Id) is null &&
                Preview(member, family, branch, person.Id, []) is null, "field permission required");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            var audit = store.CountAudit(family, admin);
            var exported = Export(member, family, branch, person.Id)!;
            var text = exported.Text;
            Check(text.Contains("1 NAME Avery /Fiction/", StringComparison.Ordinal) &&
                !text.Contains("Hidden", StringComparison.Ordinal) &&
                !text.Contains("fictional secret link", StringComparison.Ordinal) &&
                !text.Contains(" BIRT", StringComparison.Ordinal) &&
                !text.Contains(" DEAT", StringComparison.Ordinal) &&
                !text.Contains(" DATE", StringComparison.Ordinal) &&
                !text.Contains(" FAM", StringComparison.Ordinal) &&
                !text.Contains(" OBJE", StringComparison.Ordinal) &&
                !text.Contains("2 SOUR", StringComparison.Ordinal) &&
                !text.Contains(person.Id.ToString("D"), StringComparison.Ordinal),
                "export contains only isolated name, without hidden fields or links");
            Check(exported.Warnings.Count >= 2 && exported.Warnings.Any(w => w.Contains("Incomplete", StringComparison.Ordinal)) &&
                exported.Warnings.Any(w => w.Contains("SEX", StringComparison.Ordinal)), "export warnings preserved");
            var preview = Preview(member, family, branch, person.Id, Encoding.UTF8.GetBytes(text))!;
            var proposal = preview.Proposal!;
            Check(preview.Errors.Count == 0 && proposal.Graph.People.Count == 1 &&
                proposal.Graph.People[0].Events.Count == 0 && proposal.Graph.Families.Count == 0 &&
                proposal.Graph.Sources.Count == 0 && proposal.Matches.Count == 1 &&
                proposal.Matches[0].OtherId == person.Id.ToString("D") &&
                proposal.Matches[0].Scope == "candidate" && proposal.Matches[0].Kind == "possible-duplicate" &&
                proposal.Warnings.Any(w => w.Contains("Incomplete", StringComparison.Ordinal)) &&
                proposal.Warnings.Any(w => w.Contains("SEX", StringComparison.Ordinal)),
                "synthetic export re-preview matches only authorized name and retains warnings");
            var incoming = text.Replace("Avery /Fiction/", "Hidden /Fiction/", StringComparison.Ordinal);
            Check(Preview(member, family, branch, person.Id, Encoding.UTF8.GetBytes(incoming))!
                .Proposal!.Matches.Count == 0, "hidden person is not a reconciliation candidate");
            Check(Preview(member, family, branch, person.Id, [0xc3, 0x28])!.Proposal is null &&
                Preview(member, family, branch, person.Id, null)!.Errors.Count > 0 &&
                Preview(member, family, branch, person.Id,
                    Encoding.UTF8.GetBytes(text.Replace("0 TRLR", "1 NOTE private\n0 TRLR", StringComparison.Ordinal)))!
                    .Proposal is null, "malicious and invalid input rejected");
            Check(Export(Guid.Empty, family, branch, person.Id) is null &&
                Export(Guid.NewGuid(), family, branch, person.Id) is null &&
                Export(pending, family, branch, person.Id) is null &&
                Export(member, Guid.NewGuid(), branch, person.Id) is null &&
                Export(member, family, Guid.NewGuid(), person.Id) is null &&
                Export(member, family, branch, hidden.Id) is null &&
                Export(member, family, branch, Guid.NewGuid()) is null,
                "unavailable identities, scopes and people are indistinguishable");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Expect<OperationCanceledException>(() => slice.ExportAsync(member, family, branch,
                person.Id, cancelled.Token).GetAwaiter().GetResult(), "export cancellation");
            Expect<OperationCanceledException>(() => slice.PreviewAsync(member, family, branch,
                person.Id, [], cancelled.Token).GetAwaiter().GetResult(), "preview cancellation");
            Expect<ArgumentNullException>(() => new GedcomAuthorizedPersonSlice(null!), "source required");
            Check(store.CountAudit(family, admin) == audit &&
                store.ReadPeople(family, member).Count == 2 &&
                store.ReadRelationships(family, member).Count == 1, "operations do not write or approve");
            foreach (var name in new[] { "Fictional @Name@", "Fictional\tName" })
            {
                var invalid = new Person(Guid.NewGuid(), name);
                store.ReviewPerson(family, admin, store.ProposePerson(family, member, invalid), true);
                grants.GrantPerson(family, branch, admin, member, invalid.Id);
                grants.GrantDisplayName(family, branch, admin, member, invalid.Id);
                Expect<InvalidOperationException>(() => Export(member, family, branch, invalid.Id),
                    "unsupported persisted name fails export closed");
                Expect<InvalidOperationException>(() => Preview(member, family, branch, invalid.Id,
                    Encoding.UTF8.GetBytes(text)), "unsupported persisted name fails preview closed");
            }
            grants.RevokeDisplayName(family, branch, admin, member, person.Id);
            Check(Export(member, family, branch, person.Id) is null &&
                Preview(member, family, branch, person.Id, Encoding.UTF8.GetBytes(text)) is null,
                "field revocation denies both operations");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            grants.RevokePerson(family, branch, admin, member, person.Id);
            Check(Export(member, family, branch, person.Id) is null, "person revocation denies export");
            grants.GrantPerson(family, branch, admin, member, person.Id);
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            store.RevokeMember(family, admin, member);
            Check(Export(member, family, branch, person.Id) is null &&
                Preview(member, family, branch, person.Id, Encoding.UTF8.GetBytes(text)) is null,
                "membership revocation denies both operations");
            Console.WriteLine("PASS authorized single-person GEDCOM projection and preview");
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
