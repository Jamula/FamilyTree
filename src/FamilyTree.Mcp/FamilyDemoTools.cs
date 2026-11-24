using System.ComponentModel;
using System.Text.Json;
using FamilyTree.Domain;
using FamilyTree.SyntheticDemo;
using ModelContextProtocol.Server;

namespace FamilyTree.Mcp;

/// <summary>
/// Demo-only MCP tools. They cannot access a real family database and cannot
/// publish, approve or persist genealogy records. Do not log user inputs.
/// </summary>
[McpServerToolType]
public static class FamilyDemoTools
{
    [McpServerTool, Description("Describe the entirely fictional multi-generation family demo and safe MCP capabilities.")]
    public static string FamilyOverview() => JsonSerializer.Serialize(new
    {
        dataset = SyntheticFamilyCatalog.DataSetName,
        syntheticOnly = true,
        people = SyntheticFamilyCatalog.People.Count,
        relationships = SyntheticFamilyCatalog.Relationships.Count,
        generations = 5,
        toolsAreLocal = true,
        approvedWritesAllowed = false,
        draftsAreEphemeral = true
    });

    [McpServerTool, Description("List fictional demo people. The result contains no real family records.")]
    public static string ListDemoPeople(int limit = 32)
    {
        var boundedLimit = Math.Clamp(limit, 1, 50);
        return JsonSerializer.Serialize(SyntheticFamilyCatalog.People
            .Take(boundedLimit)
            .Select(p => new { id = p.Id, name = p.DisplayName }));
    }

    [McpServerTool, Description("Search by the name of a fictional demo person; case-insensitive, maximum 80 characters.")]
    public static string SearchDemoPeople(string query) =>
        McpToolErrors.Invoke(() => SearchDemoPeopleCore(query));

    private static string SearchDemoPeopleCore(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "[]";
        if (query.Length > 80) throw new ArgumentException("Search text too long.");
        return JsonSerializer.Serialize(SyntheticFamilyCatalog.People
            .Where(p => p.DisplayName.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .Take(40).Select(p => new { id = p.Id, name = p.DisplayName }));
    }

    [McpServerTool, Description("Inspect one fictional person and authorized-for-demo relationships by GUID.")]
    public static string InspectDemoPerson(string personId)
    {
        if (!Guid.TryParse(personId, out var id)) return "{\"found\":false}";
        var person = SyntheticFamilyCatalog.People.FirstOrDefault(p => p.Id == id);
        if (person is null) return "{\"found\":false}";
        var edges = SyntheticFamilyCatalog.Relationships
            .Where(r => r.FromPersonId == id || r.ToPersonId == id)
            .Select(r => new
            {
                id = r.Id,
                from = r.FromPersonId,
                to = r.ToPersonId,
                kind = r.Kind.ToString(),
                label = r.Label
            });
        return JsonSerializer.Serialize(new { found = true, person.Id,
            name = person.DisplayName, relationships = edges });
    }

    [McpServerTool, Description("List fictional relationships; use this for building test views only.")]
    public static string ListDemoRelationships(int limit = 80)
    {
        return JsonSerializer.Serialize(SyntheticFamilyCatalog.Relationships
            .Take(Math.Clamp(limit, 1, 100))
            .Select(r => new { id = r.Id, from = r.FromPersonId,
                to = r.ToPersonId, kind = r.Kind.ToString(), label = r.Label }));
    }

    [McpServerTool, Description("Stage a fictional person suggestion in private, in-memory drafts. Never publishes records.")]
    public static string ProposeDemoPerson(string displayName) =>
        McpToolErrors.Invoke(() => ProposeDemoPersonCore(displayName));

    private static string ProposeDemoPersonCore(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80 ||
            displayName.Any(char.IsControl))
            throw new ArgumentException("Provide a fictional display name of 1 to 80 characters.");
        return JsonSerializer.Serialize(DemoProposalQueue.Stage(
            "person", displayName.Trim()));
    }

    [McpServerTool, Description("Stage a fictional relationship suggestion between existing demo GUIDs. Never publishes records.")]
    public static string ProposeDemoRelationship(string fromPersonId, string toPersonId, string relationKind) =>
        McpToolErrors.Invoke(() => ProposeDemoRelationshipCore(
            fromPersonId, toPersonId, relationKind));

    private static string ProposeDemoRelationshipCore(
        string fromPersonId, string toPersonId, string relationKind)
    {
        if (!Guid.TryParse(fromPersonId, out var from) ||
            !Guid.TryParse(toPersonId, out var to) || from == to)
            throw new ArgumentException("Two distinct demo person GUIDs are required.");
        if (!SyntheticFamilyCatalog.People.Any(p => p.Id == from) ||
            !SyntheticFamilyCatalog.People.Any(p => p.Id == to))
            throw new ArgumentException("Both endpoints must be existing synthetic demo people.");
        if (!Enum.TryParse<RelationKind>(relationKind, true, out var kind) ||
            !Enum.IsDefined(kind) || kind == RelationKind.Other)
            throw new ArgumentException("Choose a defined relationship type other than Other.");
        return JsonSerializer.Serialize(DemoProposalQueue.Stage(
            "relationship", $"{from:D}|{kind}|{to:D}"));
    }

    [McpServerTool, Description("List proposals staged in this local MCP process; none has been approved or persisted.")]
    public static string ListDemoDrafts() => JsonSerializer.Serialize(DemoProposalQueue.All());
}
