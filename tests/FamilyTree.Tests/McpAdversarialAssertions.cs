using System.Collections.Concurrent;
using System.Text.Json;
using FamilyTree.Mcp;
using FamilyTree.SyntheticDemo;

internal static class McpAdversarialAssertions
{
    internal static void Run()
    {
        McpSecurityPolicyAssertions.Run();
        using (var overview = JsonDocument.Parse(FamilyDemoTools.FamilyOverview()))
        {
            Check(overview.RootElement.GetProperty("syntheticOnly").GetBoolean() &&
                !overview.RootElement.GetProperty("approvedWritesAllowed").GetBoolean() &&
                overview.RootElement.GetProperty("people").GetInt32() == 32,
                "overview cannot advertise approved private writes");
        }
        using (var low = JsonDocument.Parse(FamilyDemoTools.ListDemoPeople(-99)))
            Check(low.RootElement.GetArrayLength() == 1, "negative limit clamps to one");
        using (var high = JsonDocument.Parse(FamilyDemoTools.ListDemoPeople(int.MaxValue)))
            Check(high.RootElement.GetArrayLength() == 32, "limit remains bounded");
        using (var noResults = JsonDocument.Parse(FamilyDemoTools.SearchDemoPeople(null!)))
            Check(noResults.RootElement.GetArrayLength() == 0, "null search");
        using (var noResults = JsonDocument.Parse(FamilyDemoTools.SearchDemoPeople("  ")))
            Check(noResults.RootElement.GetArrayLength() == 0, "blank search");
        using (var noResults = JsonDocument.Parse(FamilyDemoTools.SearchDemoPeople("NotAFictionalSurname")))
            Check(noResults.RootElement.GetArrayLength() == 0, "not found search");
        using (var matches = JsonDocument.Parse(FamilyDemoTools.SearchDemoPeople(" VALE ")))
            Check(matches.RootElement.GetArrayLength() > 0, "case-fold and trim search");
        ExpectRedacted(FamilyDemoTools.SearchDemoPeople(new string('x', 81)),
            "oversized search");

        using (var malformed = JsonDocument.Parse(FamilyDemoTools.InspectDemoPerson("not-a-guid")))
            Check(!malformed.RootElement.GetProperty("found").GetBoolean(), "malformed person");
        using (var missing = JsonDocument.Parse(FamilyDemoTools.InspectDemoPerson(Guid.NewGuid().ToString())))
            Check(!missing.RootElement.GetProperty("found").GetBoolean(), "unknown person");
        using (var person = JsonDocument.Parse(FamilyDemoTools.InspectDemoPerson(
            SyntheticFamilyCatalog.PersonId(14).ToString())))
            Check(person.RootElement.GetProperty("found").GetBoolean() &&
                  person.RootElement.GetProperty("relationships").GetArrayLength() > 0,
                "authorized fictional person and links");
        using (var few = JsonDocument.Parse(FamilyDemoTools.ListDemoRelationships(-10)))
            Check(few.RootElement.GetArrayLength() == 1, "relationship negative limit bounded");
        using (var many = JsonDocument.Parse(FamilyDemoTools.ListDemoRelationships(int.MaxValue)))
            Check(many.RootElement.GetArrayLength() == SyntheticFamilyCatalog.Relationships.Count,
                "relationship large limit bounded");

        ExpectRedacted(FamilyDemoTools.ProposeDemoPerson("  "), "blank name");
        ExpectRedacted(FamilyDemoTools.ProposeDemoPerson(new string('a',81)), "long name");
        ExpectRedacted(FamilyDemoTools.ProposeDemoPerson("Fictional\nName"),
            "control in name");
        ExpectRedacted(FamilyDemoTools.ProposeDemoRelationship("bad", "bad", "Parent"),
            "malformed endpoints");
        var from=SyntheticFamilyCatalog.PersonId(1).ToString();
        var to=SyntheticFamilyCatalog.PersonId(2).ToString();
        ExpectRedacted(FamilyDemoTools.ProposeDemoRelationship(from,from,"Parent"), "self-link");
        ExpectRedacted(FamilyDemoTools.ProposeDemoRelationship(from,to,"Other"),
            "unsupported Other kind");
        ExpectRedacted(FamilyDemoTools.ProposeDemoRelationship(from,to,"999"),
            "invalid numeric relationship");
        ExpectRedacted(FamilyDemoTools.ProposeDemoRelationship(
            from,Guid.NewGuid().ToString(),"Parent"), "foreign endpoint");
        using (var draft = JsonDocument.Parse(FamilyDemoTools.ProposeDemoRelationship(
            from,to,"friend")))
            Check(!draft.RootElement.GetProperty("Approved").GetBoolean() &&
                  !draft.RootElement.GetProperty("Persisted").GetBoolean(),
                "valid proposal remains ephemeral, never approved");
        using (var allDrafts = JsonDocument.Parse(FamilyDemoTools.ListDemoDrafts()))
            Check(allDrafts.RootElement.EnumerateArray().All(
                item => !item.GetProperty("Approved").GetBoolean() &&
                        !item.GetProperty("Persisted").GetBoolean()),
                "MCP cannot publish any staged drafts");

        var buffer = new DemoDraftBuffer();
        foreach (var invalid in new[] { ("other", "valid"), ("person", ""),
            ("person", "bad\rsummary"), ("relationship", new string('x',257)) })
            Expect<ArgumentException>(()=>buffer.Stage(invalid.Item1,invalid.Item2),
                "isolated draft invalid input");
        var snapshot = buffer.All();
        Check(snapshot.Count == 0, "invalid input cannot change queue");
        var rejected = new ConcurrentBag<Exception>();
        Parallel.For(0, 150, index =>
        {
            try { buffer.Stage("person", $"Invented Test {index}"); }
            catch (InvalidOperationException exception) { rejected.Add(exception); }
        });
        var drafts = buffer.All();
        Check(drafts.Count==100 && rejected.Count==50 &&
              drafts.Select(x=>x.Id).Distinct().Count()==100 &&
              drafts.All(x=>x.Status=="pending" && !x.Approved && !x.Persisted),
            "concurrent overflow bounded without unauthorized publishing");
        Expect<InvalidOperationException>(()=>buffer.Stage("person","Overflow"),
            "overflow cannot mutate queue");
        Check(snapshot.Count==0, "previous snapshot does not change with later mutations");
        Console.WriteLine("PASS MCP invalid tools, bounded input, nonpublication, overflow and concurrency");
        McpHostRegistrationAssertions.Run();
    }

    private static void Check(bool value, string message)
    { if(!value) throw new InvalidOperationException(message); }

    private static void ExpectRedacted(string response, string message)
    {
        using var error = JsonDocument.Parse(response);
        Check(error.RootElement.GetProperty("error").GetString() ==
              "The request could not be completed.", message);
    }

    private static void Expect<T>(Action action, string message) where T:Exception
    {
        try { action(); }
        catch(T) { return; }
        throw new InvalidOperationException(message);
    }
}
