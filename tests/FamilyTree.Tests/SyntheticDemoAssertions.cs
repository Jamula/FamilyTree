using System.Text.Json;
using FamilyTree.Domain;
using FamilyTree.Mcp;
using FamilyTree.SyntheticDemo;

internal static class SyntheticDemoAssertions
{
    public static void Run()
    {
        var people = SyntheticFamilyCatalog.People;
        var edges = SyntheticFamilyCatalog.Relationships;
        Ensure(people.Count == 32, "demo must have exactly 32 fictional people");
        Ensure(people.Select(p => p.Id).Distinct().Count() == people.Count,
            "demo IDs must be unique");
        var ids = people.Select(p => p.Id).ToHashSet();
        Ensure(edges.All(e => ids.Contains(e.FromPersonId) && ids.Contains(e.ToPersonId)),
            "all edges must point to existing fictional people");
        Ensure(edges.All(e => e.FromPersonId != e.ToPersonId),
            "no accidental self-links");
        Ensure(edges.Select(e => e.Id).Distinct().Count() == edges.Count,
            "relationship IDs must be unique");
        Ensure(!edges.Any(e => e.FromPersonId == SyntheticFamilyCatalog.PersonId(30)
                             || e.ToPersonId == SyntheticFamilyCatalog.PersonId(30)),
            "intentionally disconnected fictional person");
        Ensure(edges.Any(e => e.Kind == RelationKind.Other && e.Label == "step-parent"),
            "blended family includes explicit step-parent label");
        Ensure(edges.Count(e => e.FromPersonId == SyntheticFamilyCatalog.PersonId(14)
                              && e.ToPersonId == SyntheticFamilyCatalog.PersonId(18)) == 2,
            "independent friend and neighbor links preserved");
        Ensure(HasParentChain(1, 7, 13, 19, 25),
            "five fictional generations connected by parent edges");

        using var overview = JsonDocument.Parse(FamilyDemoTools.FamilyOverview());
        Ensure(overview.RootElement.GetProperty("syntheticOnly").GetBoolean(),
            "MCP overview must explicitly identify synthetic records");
        Ensure(!overview.RootElement.GetProperty("approvedWritesAllowed").GetBoolean(),
            "MCP must not advertise approved writes");
        using var listed = JsonDocument.Parse(FamilyDemoTools.ListDemoPeople());
        Ensure(listed.RootElement.GetArrayLength() == 32,
            "MCP can list full fictional family");
        using var searched = JsonDocument.Parse(FamilyDemoTools.SearchDemoPeople("vale"));
        Ensure(searched.RootElement.GetArrayLength() > 0,
            "MCP query finds fictional surname");
        using var missing = JsonDocument.Parse(FamilyDemoTools.InspectDemoPerson(
            Guid.NewGuid().ToString("D")));
        Ensure(!missing.RootElement.GetProperty("found").GetBoolean(),
            "unknown person is not invented");
        using var proposed = JsonDocument.Parse(FamilyDemoTools.ProposeDemoPerson(
            "Fictional Draft Character"));
        Ensure(proposed.RootElement.GetProperty("Status").GetString() == "pending",
            "proposals stay pending, not approved");
        Ensure(!proposed.RootElement.GetProperty("Persisted").GetBoolean(),
            "draft proposals stay ephemeral");
        Ensure(DemoProposalQueue.All().All(p => !p.Approved && !p.Persisted),
            "no draft can be approved or persisted through MCP");
        var selfLinkRejected = false;
        try
        {
            FamilyDemoTools.ProposeDemoRelationship(
                SyntheticFamilyCatalog.PersonId(1).ToString(),
                SyntheticFamilyCatalog.PersonId(1).ToString(), "Parent");
        }
        catch (ArgumentException)
        {
            selfLinkRejected = true;
        }
        Ensure(selfLinkRejected, "self relationship proposals must be rejected");

        Console.WriteLine("PASS 14 synthetic family and safe MCP tools assertions");

        bool HasParentChain(params int[] chain)
        {
            for (var i = 0; i < chain.Length - 1; i++)
                if (!edges.Any(e => e.Kind == RelationKind.Parent &&
                                    e.FromPersonId == SyntheticFamilyCatalog.PersonId(chain[i]) &&
                                    e.ToPersonId == SyntheticFamilyCatalog.PersonId(chain[i + 1])))
                    return false;
            return true;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
