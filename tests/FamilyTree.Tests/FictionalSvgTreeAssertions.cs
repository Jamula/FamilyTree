using System.Runtime.CompilerServices;
using FamilyTree.Domain;
using FamilyTree.Web;

internal static class FictionalSvgTreeAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var center = new Person(Guid.NewGuid(), "Fictional <script> & member");
        var child = new Person(Guid.NewGuid(), "Fictional Child");
        var social = Relationship.Create(center.Id, child.Id, RelationKind.Friend);
        var parent = Relationship.Create(center.Id, child.Id, RelationKind.Parent);
        var tree = new PersonTreeProjection(center.Id,
            [new PersonTreeNode(center, 0), new PersonTreeNode(child, 1)],
            [social, parent], []);
        var options = new TreeOptions(2, 5, false, false, true);
        var html = FictionalSvgTree.Render(tree, options);
        Check(html.Contains("Fictional family generation diagram", StringComparison.Ordinal) &&
              html.Contains("Scrollable fictional family diagram", StringComparison.Ordinal),
            "diagram has title and scrollable keyboard alternative");
        Check(html.Contains("&lt;script&gt;", StringComparison.Ordinal) &&
              !html.Contains("<script>", StringComparison.Ordinal),
            "SVG text and accessible attribute values encode markup");
        Check(html.Contains("tabindex='0'", StringComparison.Ordinal) &&
              html.Contains("aria-label='Center on ", StringComparison.Ordinal),
            "people are accessible keyboard recenter links");
        Check(html.Contains("&amp;above=2", StringComparison.Ordinal) &&
              html.Contains("&amp;below=5", StringComparison.Ordinal) &&
              html.Contains("&amp;spouses=no", StringComparison.Ordinal) &&
              html.Contains("&amp;siblings=no", StringComparison.Ordinal) &&
              html.Contains("&amp;social=yes", StringComparison.Ordinal),
            "SVG recentering preserves independent tree controls");
        Check(html.Contains("stroke-dasharray='5 5'", StringComparison.Ordinal) &&
              html.Contains("#35745b", StringComparison.Ordinal),
            "social and parent edges have distinct visual semantics");
        Check(FictionalSvgTree.Render(null, options).Contains(
            "No fictional people available", StringComparison.Ordinal),
            "absent synthetic projection has safe empty state");
        var empty = new PersonTreeProjection(center.Id, [], [], []);
        Check(FictionalSvgTree.Render(empty, options).Contains(
            "No fictional people available", StringComparison.Ordinal),
            "empty synthetic projection has safe fallback");
        Throws<ArgumentNullException>(() => FictionalSvgTree.Render(tree, null!),
            "null tree options denied");
        Throws<ArgumentOutOfRangeException>(() =>
            FictionalSvgTree.Render(tree, new TreeOptions(9, 0)),
            "depth over eight denied");
        Console.WriteLine("PASS accessible SVG recentering, safe encoded names and edge styles");
    }
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
    private static void Throws<T>(Action action, string reason) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(reason);
    }
}
