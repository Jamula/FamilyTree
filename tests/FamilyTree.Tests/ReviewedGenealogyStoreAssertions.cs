using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class ReviewedGenealogyStoreAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var family=Guid.NewGuid();var author=Guid.NewGuid();var mod=Guid.NewGuid();
        var store=new ReviewedGenealogyStore(family);
        var a=new Person(Guid.NewGuid(),"Fictional A");
        var b=new Person(Guid.NewGuid(),"Fictional B");
        var p1=store.ProposePerson(family,author,a);
        Check(store.People.Count==0,"pending records not published");
        Deny(()=>store.Review(p1.Id,author,family,true,true),"self-approval");
        Deny(()=>store.Review(p1.Id,mod,Guid.NewGuid(),true,true),"cross-family moderation");
        Deny(()=>store.Review(p1.Id,mod,family,false,true),"unprivileged moderation");
        store.Review(p1.Id,mod,family,true,true);
        Check(store.People.Count==1,"approved person applied");
        var rejected=store.ProposePerson(family,author,new Person(Guid.NewGuid(),"Rejected"));
        store.Review(rejected.Id,mod,family,true,false);
        Check(store.People.Count==1,"rejected person never applied");
        var p2=store.ProposePerson(family,author,b);
        store.Review(p2.Id,mod,family,true,true);
        var edge1=Relationship.Create(a.Id,b.Id,RelationKind.Friend);
        var edge2=Relationship.Create(a.Id,b.Id,RelationKind.Neighbor);
        var e1=store.ProposeRelationship(family,author,edge1);
        var e2=store.ProposeRelationship(family,author,edge2);
        Check(store.Relationships.Count==0,"unreviewed edges withheld");
        store.Review(e1.Id,mod,family,true,true);
        store.Review(e2.Id,mod,family,true,true);
        Check(store.Relationships.Count==2 &&
            store.Relationships.Select(x=>x.Id).Distinct().Count()==2,
            "independent link types and IDs retained");
        Check(store.Audit.Count==5 && store.Audit.Select(x=>x.Revision)
            .SequenceEqual(new long[]{1,2,3,4,5}),"monotonic audits");
        Check(typeof(ReviewedGenealogyStore.AuditEntry).GetProperties().All(x=>
            !x.Name.Contains("Name",StringComparison.OrdinalIgnoreCase)),
            "audit stores no names");
        try { store.Review(e1.Id,mod,family,true,true);throw new Exception("review replay"); }
        catch(InvalidOperationException ex) when(ex.Message=="Proposal unavailable.") { }
        Deny(()=>store.ProposePerson(Guid.NewGuid(),author,new Person(Guid.NewGuid(),"Other")),
            "wrong family submission");
        Console.WriteLine("PASS in-memory approved genealogy ledger and no-self-approval");
    }
    static void Check(bool okay,string why){if(!okay)throw new Exception(why);}
    static void Deny(Action act,string why)
    {try{act();}catch(UnauthorizedAccessException){return;}throw new Exception(why);}
}
