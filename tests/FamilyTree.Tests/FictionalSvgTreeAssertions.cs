using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
        Check(html.Contains("stroke-dasharray='2 6'", StringComparison.Ordinal) &&
              html.Contains("#35745b", StringComparison.Ordinal),
            "social and parent edges have distinct dash and color semantics");
        var independentCurves = Regex.Matches(html, @"<path d='([^']+)'")
            .Select(match => match.Groups[1].Value).Distinct().ToArray();
        Check(independentCurves.Length == 2,
            "parallel friend and parent relationships must not visually overlap");
        var reversedLinks = tree with
        {
            Relationships = [
                Relationship.Create(center.Id, child.Id, RelationKind.Friend),
                Relationship.Create(child.Id, center.Id, RelationKind.Friend)
            ]
        };
        var reversedHtml = FictionalSvgTree.Render(reversedLinks, options);
        var reversedPaths = Regex.Matches(reversedHtml, @"<path d='([^']+)'")
            .Select(match => match.Groups[1].Value).Distinct().ToArray();
        Check(reversedPaths.Length == 2,
            "oppositely directed connections must have distinct paths");
        Check(html.Contains("Parent connection", StringComparison.Ordinal) &&
              html.Contains("Friend connection", StringComparison.Ordinal),
            "relationship SVG paths have accessible typed descriptions");
        var inferredTree = tree with
        {
            Relationships = [],
            InferredSiblingConnections = [new InferredSiblingConnection(center.Id, child.Id)]
        };
        Check(FictionalSvgTree.Render(inferredTree, new TreeOptions(0, 0))
              .Contains("stroke-dasharray='3 4'", StringComparison.Ordinal),
            "authorized inferred siblings are connected when parents are hidden by depth");
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
