using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class PrivateTreeReadServiceAssertions
{
    private sealed class Source : ITrustedTreeReadSource
    {
        internal TrustedTreeReadSnapshot? Snapshot;
        internal int Calls;
        public Task<TrustedTreeReadSnapshot?> LoadAsync(
            Guid viewer, Guid family, Guid branch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Snapshot);
        }
    }

    [ModuleInitializer]
    internal static void Run()
    {
        var viewer = Guid.NewGuid();
        var family = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var center = new Person(Guid.NewGuid(), "Fictional Center");
        var child = new Person(Guid.NewGuid(), "Fictional Child");
        var hidden = new Person(Guid.NewGuid(), "Fictional Hidden");
        var parentEdge = Relationship.Create(center.Id, child.Id, RelationKind.Parent);
        var hiddenEdge = Relationship.Create(center.Id, hidden.Id, RelationKind.Parent);
        var grants = new[] { new BranchVisibilityGrant(viewer, family, branch,
            MembershipState.Active, new HashSet<Guid> { center.Id, child.Id, hidden.Id },
            new HashSet<Guid> { parentEdge.Id, hiddenEdge.Id }) };
        var fields = new FieldPrivacyPolicy(family, new[] {
            new FamilyFieldGrant(viewer, family, center.Id, MembershipState.Active,
                new HashSet<ProfileField> { ProfileField.DisplayName }),
            new FamilyFieldGrant(viewer, family, child.Id, MembershipState.Active,
                new HashSet<ProfileField> { ProfileField.DisplayName }),
            new FamilyFieldGrant(viewer, family, hidden.Id, MembershipState.Active,
                new HashSet<ProfileField> { ProfileField.Contact })
        });
        var source = new Source();
        var service = new PrivateTreeReadService(source);
        var good = new TrustedTreeReadSnapshot(viewer, family, branch, true,
            [center, child, hidden], [parentEdge, hiddenEdge], grants, fields);
        source.Snapshot = good;

        AuthorizedTreeProjection? Read(Guid principal, Guid tenant, Guid scope,
            Guid selected, TreeOptions? options = null, CancellationToken token = default) =>
            service.GetAsync(principal, tenant, scope, selected,
                options ?? new TreeOptions(), token).GetAwaiter().GetResult();

        var result = Read(viewer, family, branch, center.Id);
        Check(result is not null && result.Nodes.Count == 2 &&
              result.Edges.Count == 1 &&
              result.Edges[0].RelationshipId == parentEdge.Id,
            "authorized center exposes only fully allowed nodes and edges");
        Check(!result!.Nodes.Any(x => x.PersonId == hidden.Id ||
              x.DisplayName == hidden.DisplayName), "hidden person not leaked");
        Check(Read(viewer, family, branch, hidden.Id) is null &&
              Read(viewer, family, branch, Guid.NewGuid()) is null,
            "unknown and unauthorized center responses equivalent");
        Check(Read(Guid.NewGuid(), family, branch, center.Id) is null &&
              Read(viewer, Guid.NewGuid(), branch, center.Id) is null &&
              Read(viewer, family, Guid.NewGuid(), center.Id) is null,
            "cross-principal, family and branch read denied");
        var before = source.Calls;
        Check(Read(Guid.Empty, family, branch, center.Id) is null &&
              Read(viewer, family, branch, Guid.Empty) is null &&
              source.Calls == before,
            "invalid IDs refused before querying private storage");
        source.Snapshot = good with { ActiveMembership = false };
        Check(Read(viewer, family, branch, center.Id) is null,
            "revoked membership denies entire graph");
        source.Snapshot = good with { ViewerId = Guid.NewGuid() };
        Check(Read(viewer, family, branch, center.Id) is null,
            "mismatched trusted principal cannot be used");
        source.Snapshot = good with { FamilyId = Guid.NewGuid() };
        Check(Read(viewer, family, branch, center.Id) is null,
            "wrong snapshot family denied");
        source.Snapshot = good with { BranchId = Guid.NewGuid() };
        Check(Read(viewer, family, branch, center.Id) is null,
            "wrong snapshot branch denied");
        source.Snapshot = good with { FieldGrants = new FieldPrivacyPolicy(Guid.NewGuid(), []) };
        Expect<InvalidOperationException>(() => Read(viewer, family, branch, center.Id),
            "cross-family field configuration rejected");
        source.Snapshot = good with { People = [center, center] };
        Expect<InvalidOperationException>(() => Read(viewer, family, branch, center.Id),
            "duplicate Person IDs rejected before projection");
        source.Snapshot = good with { Relationships =
            [new Relationship(Guid.Empty, center.Id, child.Id, RelationKind.Parent)] };
        Expect<InvalidOperationException>(() => Read(viewer, family, branch, center.Id),
            "malformed relationship rejected");
        source.Snapshot = null;
        Check(Read(viewer, family, branch, center.Id) is null,
            "missing trusted snapshot denied");
        source.Snapshot = good;
        Expect<ArgumentNullException>(() => new PrivateTreeReadService(null!),
            "no source cannot be constructed");
        Expect<ArgumentNullException>(() => service.GetAsync(
            viewer, family, branch, center.Id, null!).GetAwaiter().GetResult(),
            "missing options rejected");
        Expect<ArgumentOutOfRangeException>(() => Read(
            viewer, family, branch, center.Id, new TreeOptions(9, 0)),
            "depth above bounds rejected");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Expect<OperationCanceledException>(() => Read(
            viewer, family, branch, center.Id, token: cancellation.Token),
            "cancelled queries do not read private storage");
        Console.WriteLine("PASS trusted tree read projection, revocation, field/branch isolation and errors");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    private static void Expect<T>(Action action, string reason) where T : Exception
    {
        var caught = false;
        try { action(); } catch (T) { caught = true; }
        if (!caught) throw new InvalidOperationException(reason);
    }
}
