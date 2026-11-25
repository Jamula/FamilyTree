using System.Text.RegularExpressions;
using FamilyTree.Domain;
using FamilyTree.Web;

internal static class PrivateTreeUiAssertions
{
    private static readonly Guid FamilyId = Guid.Parse("a14eb8ac-55b0-43f4-a3ef-17a998d9b806");
    private static readonly Guid BranchId = Guid.Parse("b4d811ec-ff03-43ae-8dcb-67ace405b568");

    public static void Run()
    {
        VerifyDepthAndKeyboardControls();
        VerifyMembershipControls();
        VerifyUnavailableContentIsGeneric();
    }

    private static void VerifyDepthAndKeyboardControls()
    {
        var tree = CreateDeepTree();
        var depthZero = Render(tree, 0, 0);
        Ensure(depthZero.Contains("Synthetic person 8", StringComparison.Ordinal) &&
            !depthZero.Contains("Synthetic person 7", StringComparison.Ordinal) &&
            !depthZero.Contains("Synthetic person 9", StringComparison.Ordinal),
            "depth zero renders only the synthetic center person");

        var depthOne = Render(tree, 1, 1);
        Ensure(depthOne.Contains("Synthetic person 7", StringComparison.Ordinal) &&
            depthOne.Contains("Synthetic person 9", StringComparison.Ordinal) &&
            !depthOne.Contains("Synthetic person 6", StringComparison.Ordinal) &&
            !depthOne.Contains("Synthetic person 10", StringComparison.Ordinal),
            "depth one renders only one synthetic generation in either direction");

        var depthEight = Render(tree, 8, 8);
        var recenterButtons = Regex.Matches(depthEight,
            "<button type='submit'>Center on Synthetic person \\d+</button>");
        Ensure(recenterButtons.Count == 17 &&
            depthEight.Contains("<label for='above'>Generations above</label><select id='above'",
                StringComparison.Ordinal) &&
            depthEight.Contains("<label for='below'>Generations below</label><select id='below'",
                StringComparison.Ordinal) &&
            depthEight.Contains("Update depth</button>", StringComparison.Ordinal) &&
            depthEight.Contains(":focus-visible", StringComparison.Ordinal),
            "depth eight exposes keyboard-operable recenter buttons and labelled depth controls");
    }

    private static void VerifyMembershipControls()
    {
        var html = Render(CreateDeepTree(), 1, 1);
        Ensure(html.Contains("<section aria-labelledby='membership-controls'>",
                StringComparison.Ordinal) &&
            html.Contains("<label for='membership-invite'>Principal ID</label>",
                StringComparison.Ordinal) &&
            html.Contains("<label for='membership-accept'>Invitation ID</label>",
                StringComparison.Ordinal) &&
            html.Contains("<label for='membership-approve'>Member ID</label>",
                StringComparison.Ordinal) &&
            html.Contains("<label for='membership-revoke'>Member ID</label>",
                StringComparison.Ordinal) &&
            html.Contains("name='confirmRevoke' type='checkbox' value='true' required",
                StringComparison.Ordinal),
            "membership forms are labelled, keyboard-submittable, and require explicit revoke confirmation");
    }

    private static void VerifyUnavailableContentIsGeneric()
    {
        var tree = CreateDeepTree();
        var unavailable = PrivateTreeRoutes.RenderWorkspace(
            new PrivateTreeApiStatus(true, Array.Empty<string>(), true),
            "synthetic-csrf-token", FamilyId, BranchId, "Secret query",
            [new AuthorizedPersonSummary(tree.CenterPersonId, "Synthetic person 8")],
            4, 4, unavailable: true);
        Ensure(unavailable.Contains("Private records are unavailable", StringComparison.Ordinal) &&
            !unavailable.Contains("Secret query", StringComparison.Ordinal) &&
            !unavailable.Contains("Synthetic person 8", StringComparison.Ordinal) &&
            !unavailable.Contains(tree.CenterPersonId.ToString("D"), StringComparison.Ordinal),
            "unavailable UI does not render private search terms or person identifiers");
    }

    private static string Render(AuthorizedTreeProjection tree, int above, int below) =>
        PrivateTreeRoutes.RenderWorkspace(
            new PrivateTreeApiStatus(true, Array.Empty<string>(), true),
            "synthetic-csrf-token", FamilyId, BranchId, null, null, above, below,
            tree: tree);

    private static AuthorizedTreeProjection CreateDeepTree()
    {
        var personIds = Enumerable.Range(0, 17)
            .Select(index => Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"))
            .ToArray();
        var nodes = personIds.Select((personId, index) =>
            new AuthorizedTreeNode(personId, $"Synthetic person {index}", index - 8))
            .ToArray();
        var edges = Enumerable.Range(0, 16)
            .Select(index => new AuthorizedTreeEdge(
                Guid.Parse($"10000000-0000-0000-0000-{index + 1:D12}"),
                personIds[index],
                personIds[index + 1],
                RelationKind.Parent))
            .ToArray();
        return new AuthorizedTreeProjection(personIds[8], nodes, edges, []);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Private tree UI assertion failed: {message}");
    }
}
