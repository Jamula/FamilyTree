using System.Runtime.CompilerServices;
using FamilyTree.Web;

internal static class FictionalWorkbenchAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var state = new SyntheticReviewWorkbench();
        var seed = state.Read();
        Check(seed.People.Count == 2 && seed.Relationships.Count == 0 &&
              seed.PendingKind is null && seed.Audit.Count == 2 &&
              !seed.HasUndo && seed.RemainingGuests == 6,
            "fixed fictional seed without private data");
        Check(!state.Execute("approve") && !state.Execute("undo") &&
              !state.Execute("reject") && !state.Execute("anything") &&
              !state.Execute(null),
            "invalid or premature actions denied");
        Check(state.Execute("propose-person"),"submit invented guest");
        Check(state.Read().PendingKind == "AddPerson" &&
              state.Read().People.Count == 2,
            "pending person cannot appear before approval");
        Check(!state.Execute("propose-person") && !state.Execute("propose-link") &&
              !state.Execute("undo"),"only one pending proposal and no undo during review");
        Check(state.Execute("reject") && state.Read().People.Count == 2 &&
              state.Read().PendingKind is null,
            "independent rejection preserves approved graph");
        Check(state.Execute("propose-person") && state.Execute("approve"),
            "approve a fictional guest");
        Check(state.Read().People.Count == 3 && state.Read().HasUndo &&
              state.Read().People.All(p => p.DisplayName.StartsWith("Fictional",
                  StringComparison.Ordinal)),
            "only generated invented names can enter the workbench");
        Check(state.Execute("propose-link"),"propose fictional friendship");
        Check(state.Read().Relationships.Count == 0,
            "link requires independent approval");
        Check(state.Execute("approve") && state.Read().Relationships.Count == 1,
            "review approves relation");
        Check(state.Execute("undo") && state.Read().Relationships.Count == 0,
            "most recent link can be reversed");
        Check(state.Execute("undo") && state.Read().People.Count == 2,
            "approved guest may be undone after linked edge");
        Check(!state.Execute("undo"),"seed records cannot be undone");
        var other=new SyntheticReviewWorkbench();
        Check(other.Read().People.Count == 2 && other.Read().Audit.Count == 2,
            "independent in-memory fictional workspace starts fresh");
        for(var i = 0;i < 6;i++)
        {
            Check(other.Execute("propose-person") && other.Execute("approve"),
                "six-guest upper limit can be reached");
        }
        Check(!other.Execute("propose-person") &&
              other.Read().RemainingGuests == 0,
            "guest limit rejects further creation");
        Check(state.Read().Audit.Count == 7,
            "rejected, approved and reversed changes append audit events");
        Console.WriteLine("PASS fictional workbench create, link, approve, reject, undo, limits and errors");
    }
    private static void Check(bool yes,string reason)
    {if(!yes)throw new InvalidOperationException(reason);}
}
