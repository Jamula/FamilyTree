using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class ReversibleAuditAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var family=Guid.NewGuid();var author=Guid.NewGuid();var reviewer=Guid.NewGuid();
        var store=new ReviewedGenealogyStore(family);
        var first=new Person(Guid.NewGuid(),"Synthetic Ada");
        var second=new Person(Guid.NewGuid(),"Synthetic Bea");
        var a=store.ProposePerson(family,author,first);
        var b=store.ProposePerson(family,author,second);
        var revA=store.Review(a.Id,reviewer,family,true,true).Revision;
        store.Review(b.Id,reviewer,family,true,true);
        var rel=Relationship.Create(first.Id,second.Id,RelationKind.Parent);
        var proposed=store.ProposeRelationship(family,author,rel);
        var revLink=store.Review(proposed.Id,reviewer,family,true,true).Revision;
        Expect<InvalidOperationException>(()=>store.Undo(revA,reviewer,family,true),
            "cannot delete a person with approved relationships");
        Expect<UnauthorizedAccessException>(()=>store.Undo(revLink,author,family,true),
            "original author cannot reverse own edit");
        Expect<UnauthorizedAccessException>(()=>store.Undo(revLink,reviewer,
            Guid.NewGuid(),true),"cross-family reversal denied");
        Expect<UnauthorizedAccessException>(()=>store.Undo(revLink,reviewer,
            family,false),"non-moderator reversal denied");
        var undoEdge=store.Undo(revLink,reviewer,family,true);
        Check(undoEdge.Reverted && !undoEdge.Approved &&
              store.Relationships.Count==0,"approved edge reversed");
        var undoPerson=store.Undo(revA,reviewer,family,true);
        Check(undoPerson.Reverted && store.People.Count==1,
            "approved person reversed after dependencies");
        Expect<InvalidOperationException>(()=>store.Undo(revLink,reviewer,
            family,true),"cannot replay an undo");
        Expect<InvalidOperationException>(()=>store.Undo(99999,reviewer,
            family,true),"unknown revision cannot be reversed");
        Check(store.Audit.Count==5 &&
              store.Audit.Select(x=>x.Revision).SequenceEqual(new long[]{1,2,3,4,5}),
            "undo actions append sequential audit entries");
        Check(store.Audit.All(x=>!x.GetType().GetProperties().Any(p=>
            p.Name.Contains("Name",StringComparison.OrdinalIgnoreCase))),
            "audit never stores names");
        Console.WriteLine("PASS reversible append-only genealogy audit and negative tests");
    }
    private static void Check(bool valid,string why)
    {if(!valid)throw new InvalidOperationException(why);}
    private static void Expect<T>(Action action,string why) where T:Exception
    {
        var raised=false;
        try{action();}catch(T){raised=true;}
        Check(raised,why);
    }
}
