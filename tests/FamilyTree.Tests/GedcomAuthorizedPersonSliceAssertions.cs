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
        var otherMember = Guid.NewGuid();
        var pending = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var person = new Person(Guid.NewGuid(), "Avery /Fiction/", true);
        var hidden = new Person(Guid.NewGuid(), "Hidden /Fiction/");
        try
        {
            using var store = new SqliteFamilyStore(path, key);
            store.CreateFictionalFamily(family, admin);
            Invite(store, family, admin, member, true);
            Invite(store, family, admin, otherMember, true);
            Invite(store, family, admin, pending, false);
            foreach (var p in new[] { person, hidden })
                store.ReviewPerson(family, admin, store.ProposePerson(family, member, p), true);
            var edge = Relationship.Create(person.Id, hidden.Id, RelationKind.Parent, "fictional secret link");
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
            grants.GrantPerson(family, otherBranch, admin, member, person.Id);
            grants.GrantPerson(family, branch, admin, otherMember, person.Id);
            grants.GrantDisplayName(family, otherBranch, admin, member, person.Id);
            Check(Export(member, family, branch, person.Id) is null &&
                Preview(member, family, branch, person.Id, []) is null,
                "field grant in another branch cannot authorize center");
            grants.GrantDisplayName(family, branch, admin, otherMember, person.Id);
            Check(Export(member, family, branch, person.Id) is null &&
                Preview(member, family, branch, person.Id, []) is null,
                "field grants from another branch or principal cannot authorize center");
            grants.GrantDisplayName(family, branch, admin, member, person.Id);
            store.ProposePerson(family, member, new Person(Guid.NewGuid(), "Pending /Fiction/"));
            store.ProposeRelationship(family, member, Relationship.Create(person.Id, hidden.Id, RelationKind.Friend));
            var audit = store.CountAudit(family, admin);
            var originalPeople = store.ReadPeople(family, member).OrderBy(p => p.Id).ToArray();
            var originalRelationships = store.ReadRelationships(family, member).OrderBy(r => r.Id).ToArray();
            var pendingPeople = store.ListPendingPeople(family, admin).ToArray();
            var pendingRelationships = store.ListPendingRelationships(family, admin).ToArray();
            Check(pendingPeople.Length == 1 && pendingRelationships.Length == 1,
                "nonempty synthetic review inboxes establish baseline");
            var hiddenInput = Export(member, family, branch, person.Id)!.Text.Replace(
                "Avery /Fiction/", "Hidden /Fiction/", StringComparison.Ordinal);
            Check(Export(member, family, branch, hidden.Id) is null &&
                Preview(member, family, branch, hidden.Id, Encoding.UTF8.GetBytes(hiddenInput)) is null &&
                Preview(member, family, branch, person.Id, Encoding.UTF8.GetBytes(hiddenInput))!
                    .Proposal!.Matches.Count == 0,
                "hidden center denied and hidden person excluded from reconciliation");
            grants.GrantDisplayName(family, branch, admin, member, hidden.Id);
            Check(originalRelationships.Length == 1 && originalRelationships[0].Id == edge.Id,
                "parent relationship is stored before preview");
            Check(Export(member, family, branch, hidden.Id)!.Text.Contains("Hidden /Fiction/", StringComparison.Ordinal),
                "both endpoints independently visible");
            var snapshot = source.LoadAsync(member, family, branch, person.Id, CancellationToken.None)
                .GetAwaiter().GetResult()!;
            Check(snapshot.People.Count == 1 && snapshot.People[0].Id == person.Id &&
                snapshot.Relationships.Count == 0, "SQLite snapshot excludes stored ungranted parent link");
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
                .Proposal!.Matches.Count == 0, "other visible person is not a reconciliation candidate");
            var multiple = """
                0 HEAD
                1 GEDC
                2 VERS 7.0
                0 @I3@ INDI
                1 NAME Hidden /Fiction/
                0 @I2@ INDI
                1 NAME avery /fiction/
                1 BIRT
                2 DATE 1901
                0 @I1@ INDI
                1 NAME Avery /Fiction/
                1 BIRT
                2 DATE 1900
                0 TRLR
                """;
            var expectedMatches = new[] {
                new SyntheticGedcomMatch("@I1@", person.Id.ToString("D"), "candidate", "possible-duplicate"),
                new SyntheticGedcomMatch("@I1@", "@I2@", "incoming", "birth-date-conflict"),
                new SyntheticGedcomMatch("@I2@", person.Id.ToString("D"), "candidate", "possible-duplicate")
            };
            var reordered = """
                0 HEAD
                1 GEDC
                2 VERS 7.0
                0 @I1@ INDI
                1 NAME Avery /Fiction/
                1 BIRT
                2 DATE 1900
                0 @I3@ INDI
                1 NAME Hidden /Fiction/
                0 @I2@ INDI
                1 NAME avery /fiction/
                1 BIRT
                2 DATE 1901
                0 TRLR
                """;
            var multiPreview = Preview(member, family, branch, person.Id, Encoding.UTF8.GetBytes(multiple))!;
            Check(multiPreview.Errors.Count == 0 && multiPreview.Proposal!.Matches.SequenceEqual(expectedMatches) &&
                Preview(member, family, branch, person.Id, Encoding.UTF8.GetBytes(reordered))!
                    .Proposal!.Matches.SequenceEqual(expectedMatches),
                "reconciliation is deterministic across record order and limited to the requested center");
            var otherCenterMatches = Preview(member, family, branch, hidden.Id, Encoding.UTF8.GetBytes(multiple))!
                .Proposal!.Matches;
            Check(otherCenterMatches.SequenceEqual(new[] {
                new SyntheticGedcomMatch("@I1@", "@I2@", "incoming", "birth-date-conflict"),
                new SyntheticGedcomMatch("@I3@", hidden.Id.ToString("D"), "candidate", "possible-duplicate")
            }), "changing center changes candidate without exposing other visible people");
            var danglingSource = text.Replace("0 TRLR", "1 BIRT\n2 SOUR @S1@\n0 TRLR", StringComparison.Ordinal);
            var malformedSource = text.Replace("0 TRLR", "0 @S1@ SOUR\n1 TITL\n0 TRLR", StringComparison.Ordinal);
            var danglingFamily = text.Replace("0 TRLR", "0 @F1@ FAM\n1 HUSB @I1@\n1 CHIL @MISSING@\n0 TRLR", StringComparison.Ordinal);
            foreach (var invalid in new[] { danglingSource, malformedSource, danglingFamily })
            {
                var invalidPreview = Preview(member, family, branch, person.Id, Encoding.UTF8.GetBytes(invalid))!;
                Check(invalidPreview.Proposal is null && invalidPreview.Errors.Count > 0,
                    "authorized preview rejects malformed or dangling source and family references");
            }
            Check(Preview(member, family, branch, person.Id, [0xc3, 0x28])!.Proposal is null &&
                Preview(member, family, branch, person.Id, null)!.Errors.Count > 0 &&
                Preview(member, family, branch, person.Id, new byte[256_001]) is { Proposal: null, Errors.Count: > 0 } &&
                Preview(member, family, branch, person.Id,
                    Encoding.UTF8.GetBytes(text.Replace("0 TRLR", "1 NOTE private\n0 TRLR", StringComparison.Ordinal)))!
                    .Proposal is null, "malicious and invalid input rejected");
            Check(Export(Guid.Empty, family, branch, person.Id) is null &&
                Export(Guid.NewGuid(), family, branch, person.Id) is null &&
                Export(pending, family, branch, person.Id) is null &&
                Export(member, Guid.NewGuid(), branch, person.Id) is null &&
                Export(member, family, Guid.NewGuid(), person.Id) is null &&
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
                store.ReadPeople(family, member).OrderBy(p => p.Id).SequenceEqual(originalPeople) &&
                store.ReadRelationships(family, member).OrderBy(r => r.Id).SequenceEqual(originalRelationships) &&
                store.ListPendingPeople(family, admin).SequenceEqual(pendingPeople) &&
                store.ListPendingRelationships(family, admin).SequenceEqual(pendingRelationships),
                "valid, invalid and cancelled previews do not change graph, review inboxes or audit");
            foreach (var invalid in new[] { "Fictional @Name@", "Fictional\tName" }
                .Select(name => new Person(Guid.NewGuid(), name)))
            {
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
            SyntheticRelationshipProjection();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(folder, true);
        }
    }

    private static void SyntheticRelationshipProjection()
    {
        var viewer = Guid.NewGuid();
        var family = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var parent = new Person(Guid.NewGuid(), "Morgan /Fiction/");
        var child = new Person(Guid.NewGuid(), "Riley /Fiction/");
        var edge = Relationship.Create(parent.Id, child.Id, RelationKind.Parent);
        var people = new[] { parent, child };
        var grant = new BranchVisibilityGrant(viewer, family, branch, MembershipState.Active,
            new HashSet<Guid> { parent.Id, child.Id }, new HashSet<Guid> { edge.Id });
        FamilyFieldGrant Name(Guid principal, Guid f, Guid p) =>
            new(principal, f, p, MembershipState.Active, new HashSet<ProfileField> { ProfileField.DisplayName });
        var fields = new FieldPrivacyPolicy(family, [Name(viewer, family, parent.Id), Name(viewer, family, child.Id)]);
        AuthorizedTreeProjection? Project(Guid v, Guid f, Guid b, Guid center,
            BranchVisibilityGrant g, FieldPrivacyPolicy policy) =>
            FieldRedactedTreeQuery.ProjectAsync(v, f, b, center, people, [edge], new TreeOptions(), [g], policy)
                .GetAwaiter().GetResult();
        var authorized = Project(viewer, family, branch, parent.Id, grant, fields)!;
        Check(authorized.Nodes.Select(n => n.PersonId).ToHashSet().SetEquals([parent.Id, child.Id]) &&
            authorized.Edges.Count == 1 && authorized.Edges[0].RelationshipId == edge.Id,
            "explicit synthetic field, endpoint and relationship grants permit parent link");
        var noEdge = Project(viewer, family, branch, parent.Id,
            grant with { VisibleRelationships = new HashSet<Guid>() }, fields)!;
        Check(noEdge.Nodes.Count == 1 && noEdge.Edges.Count == 0,
            "visible endpoints alone cannot authorize an edge");
        var localEndpoints = grant with { VisibleRelationships = new HashSet<Guid>() };
        foreach (var crossScope in new[] {
            grant with { PrincipalId = Guid.NewGuid() },
            grant with { BranchId = Guid.NewGuid() }
        }.Select(foreignEdgeGrant => FieldRedactedTreeQuery.ProjectAsync(viewer, family, branch, parent.Id,
            people, [edge], new TreeOptions(), [localEndpoints, foreignEdgeGrant], fields)
            .GetAwaiter().GetResult()))
        {
            Check(crossScope is not null && crossScope.Nodes.Count == 1 &&
                crossScope.Nodes[0].PersonId == parent.Id && crossScope.Edges.Count == 0,
                "foreign principal or branch edge grant cannot combine with local endpoints");
        }
        var noChildField = new FieldPrivacyPolicy(family, [Name(viewer, family, parent.Id)]);
        var noChild = Project(viewer, family, branch, parent.Id, grant, noChildField)!;
        Check(noChild.Nodes.Count == 1 && noChild.Edges.Count == 0 &&
            Project(viewer, family, branch, child.Id, grant, noChildField) is null,
            "child display-name grant required");
        var noParentField = new FieldPrivacyPolicy(family, [Name(viewer, family, child.Id)]);
        var noParent = Project(viewer, family, branch, child.Id, grant, noParentField)!;
        Check(noParent.Nodes.Count == 1 && noParent.Edges.Count == 0 &&
            Project(viewer, family, branch, parent.Id, grant, noParentField) is null,
            "parent display-name grant required");
        var wrongFields = new FieldPrivacyPolicy(family, [
            Name(Guid.NewGuid(), family, parent.Id), Name(viewer, Guid.NewGuid(), parent.Id),
            Name(viewer, family, child.Id)]);
        Check(Project(viewer, family, branch, parent.Id, grant, wrongFields) is null &&
            Project(Guid.NewGuid(), family, branch, parent.Id, grant, fields) is null &&
            Project(viewer, Guid.NewGuid(), branch, parent.Id, grant, fields) is null &&
            Project(viewer, family, Guid.NewGuid(), parent.Id, grant, fields) is null,
            "principal, family and branch scopes cannot be combined");

        // Test-only mapping of the specifically authorized synthetic projection, not a persistence adapter.
        var byId = authorized.Nodes.ToDictionary(n => n.PersonId);
        var subset = new SyntheticGedcomGraph([
            new("@I1@", byId[parent.Id].DisplayName, []),
            new("@I2@", byId[child.Id].DisplayName, [])
        ], [new("@F1@", [new("HUSB", "@I1@")], ["@I2@"])], []);
        var serialized = GedcomSyntheticSlice.Export(new(subset));
        var roundTrip = GedcomSyntheticSlice.Preview(Encoding.UTF8.GetBytes(serialized.Text));
        Check(roundTrip.Errors.Count == 0 && roundTrip.Proposal is not null &&
            roundTrip.Proposal.Graph.People.Select(p => (p.Id, p.Name))
                .SequenceEqual(subset.People.Select(p => (p.Id, p.Name))) &&
            roundTrip.Proposal.Graph.Families.Single().Parents.SequenceEqual(subset.Families.Single().Parents) &&
            roundTrip.Proposal.Graph.Families.Single().Children.SequenceEqual(subset.Families.Single().Children),
            "explicitly constructed authorized synthetic subset serializes and round-trips");
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
