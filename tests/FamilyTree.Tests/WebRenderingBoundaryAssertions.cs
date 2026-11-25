using System.Runtime.CompilerServices;
using FamilyTree.Domain;
using FamilyTree.Web;

internal static class WebRenderingBoundaryAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var center = new Person(Guid.NewGuid(), "Fictional Render Center");
        var sibling = new Person(Guid.NewGuid(), "Fictional Render Sibling");
        var edge = Relationship.Create(center.Id, sibling.Id, RelationKind.Sibling);
        var tree = new PersonTreeProjection(center.Id,
            [new PersonTreeNode(center, 0), new PersonTreeNode(sibling, 0)],
            [edge], []);
        var html = FictionalSvgTree.Render(tree, new TreeOptions());
        Check(html.Contains("stroke-dasharray='6 3'", StringComparison.Ordinal) &&
            html.Contains("Sibling connection", StringComparison.Ordinal),
            "explicit sibling relation uses an accessible sibling path style");

        var nameless = tree with
        {
            Relationships = [],
            Nodes = [new PersonTreeNode(new Person(Guid.NewGuid(), null!), 0)]
        };
        var fallback = FictionalSvgTree.Render(nameless, new TreeOptions());
        Check(fallback.Contains("<text", StringComparison.Ordinal),
            "null display name renders without creating malformed SVG");
        Console.WriteLine("PASS sibling path and defensive SVG name outcomes");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
