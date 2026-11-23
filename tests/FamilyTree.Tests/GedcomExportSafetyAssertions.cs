using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class GedcomExportSafetyAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var viewer = Guid.NewGuid();
        var family = Guid.NewGuid();
        var first = new Person(Guid.NewGuid(), "Fictional Visible");
        var child = new Person(Guid.NewGuid(), "Fictional Child");
        var rel = Relationship.Create(first.Id, child.Id, RelationKind.Parent);
        var auth = Grant(viewer, family, [first.Id, child.Id], [rel.Id]);

        // Only visible members and edges may appear, even if source has extra data.
        var hidden = new Person(Guid.NewGuid(), "PRIVATE NEVER EXPORTED");
        var exported = Export(viewer, [first, hidden, child], [rel], auth);
        Check(!exported.Contains("PRIVATE", StringComparison.Ordinal) &&
              GedcomSevenSubset.Preview(exported).Valid, "privacy safe GEDCOM output");

        Throws<ArgumentNullException>(() => Export(viewer, null!, [rel], auth),
            "null people rejected");
        Throws<ArgumentNullException>(() => Export(viewer, [first], null!, auth),
            "null relations rejected");
        Throws<ArgumentNullException>(() => Export(viewer, [first], [rel], null!),
            "null authorization rejected");

        Throws<InvalidOperationException>(() => Export(viewer,
            [first, first with { DisplayName = "Duplicate" }], [], auth),
            "duplicate person identity rejected, not leaked");
        Throws<InvalidOperationException>(() => Export(viewer,
            [new Person(Guid.Empty, "Invalid")], [], auth), "empty person identifier");
        Throws<InvalidOperationException>(() => Export(viewer,
            [first, child], [rel, rel], auth), "duplicate relationship identity");
        Throws<InvalidOperationException>(() => Export(viewer,
            [first], [null!], auth), "null relationship rejected");
        Throws<InvalidOperationException>(() => Export(viewer,
            [first], [new Relationship(Guid.Empty, first.Id, child.Id, RelationKind.Parent)], auth),
            "empty relationship ID");

        foreach (var person in new[] { "Fictional\0Hidden", "Fictional\u0007Hidden",
            "Fictional@Hidden", "Fictional\nHidden" }
            .Select(invalid => new Person(Guid.NewGuid(), invalid)))
        {
            var scoped = Grant(viewer, family, [person.Id], []);
            Throws<InvalidOperationException>(() => Export(viewer, [person], [], scoped),
                "unsafe GEDCOM control character rejected");
        }

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Throws<OperationCanceledException>(() =>
                GedcomSevenSubset.ExportAuthorizedPreviewAsync(
                    viewer, [first], [], auth, cancelled.Token).GetAwaiter().GetResult(),
                "pre-cancelled export refused");
        }

        var many = Enumerable.Range(0, 10_001)
            .Select(i => new Person(Guid.NewGuid(), $"Synthetic {i}")).ToArray();
        var denied = Grant(Guid.NewGuid(), family, [], []);
        Throws<InvalidOperationException>(() => Export(viewer, many, [], denied),
            "unbounded hidden input rejected before completion");

        var manyVisible = many.Take(1_001).ToArray();
        var allVisible = Grant(viewer, family, manyVisible.Select(x => x.Id), []);
        Throws<InvalidOperationException>(() => Export(viewer, manyVisible, [], allVisible),
            "oversized visible export rejected");

        var allowedEdges = Enumerable.Range(0, 1_501).Select(_ =>
            Relationship.Create(first.Id, child.Id, RelationKind.Parent)).ToArray();
        var allowedEdgeScope = Grant(viewer, family, [first.Id, child.Id],
            allowedEdges.Select(x => x.Id));
        var upToLimit = Export(viewer, [first, child], allowedEdges.Take(1_500),
            allowedEdgeScope);
        Check(GedcomSevenSubset.Preview(upToLimit).Valid &&
              GedcomSevenSubset.Preview(upToLimit).Families.Count == 1_500,
            "1,500 approved parent edges accepted at the export limit");
        Throws<InvalidOperationException>(() => Export(viewer, [first, child],
            allowedEdges, allowedEdgeScope),
            "1,501 approved parent edges rejected independently of file length");

        var manyLinks = Enumerable.Range(0, 20_001).Select(_ =>
            Relationship.Create(first.Id, child.Id, RelationKind.Friend)).ToArray();
        Throws<InvalidOperationException>(() => Export(viewer,
            [first, child], manyLinks, auth), "oversized relation input rejected");

        Console.WriteLine("PASS GEDCOM bounds, duplicate identities, controls, cancellation and error paths");
    }

    private static MembershipScopedAuthorization Grant(Guid viewer, Guid family,
        IEnumerable<Guid> personIds, IEnumerable<Guid> relationshipIds) =>
        new(family, [new ScopedFamilyGrant(viewer, family, MembershipState.Active,
            personIds.ToHashSet(), relationshipIds.ToHashSet())]);

    private static string Export(Guid viewer, IEnumerable<Person> people,
        IEnumerable<Relationship> relationships, IFamilyAuthorization authorization) =>
        GedcomSevenSubset.ExportAuthorizedPreviewAsync(viewer, people, relationships,
            authorization).GetAwaiter().GetResult();

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
