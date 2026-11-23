using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class RelationshipProvenanceAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var family=Guid.NewGuid();var branch=Guid.NewGuid();
        var a=Guid.NewGuid();var b=Guid.NewGuid();
        var friend=Relationship.Create(a,b,RelationKind.Friend);
        var neighbor=Relationship.Create(a,b,RelationKind.Neighbor);
        var parent=Relationship.Create(a,b,RelationKind.Parent);
        var since=new DateOnly(2010,1,1);
        var until=new DateOnly(2015,12,31);
        var first=RelationshipProvenance.Create(family,branch,friend,
            AssociationCategory.Social,"Synthetic letter",since,until);
        var second=RelationshipProvenance.Create(family,branch,neighbor,
            AssociationCategory.Social,"Fictional witness");
        Check(first.Link.Id!=second.Link.Id && first.StartDate==since &&
            first.EndDate==until && first.Category==AssociationCategory.Social,
            "independent sources and dates");
        Check(RelationshipProvenance.Create(family,branch,parent,
            AssociationCategory.Genealogical,"Sample record").Category ==
            AssociationCategory.Genealogical,"family association");
        Reject(()=>RelationshipProvenance.Create(family,branch,parent,
            AssociationCategory.Social,"Sample record"),"family cannot be social");
        Reject(()=>RelationshipProvenance.Create(family,branch,friend,
            AssociationCategory.Genealogical,"Sample record"),"friend cannot be ancestor");
        Reject(()=>RelationshipProvenance.Create(family,branch,friend,
            AssociationCategory.Social,"Sample record",until,since),"reversed date range");
        Reject(()=>RelationshipProvenance.Create(family,branch,friend,
            AssociationCategory.Social," "),"missing citation");
        Reject(()=>RelationshipProvenance.Create(family,branch,friend,
            AssociationCategory.Social,"x\nprivate"),"multiline citation");
        Reject(()=>RelationshipProvenance.Create(Guid.Empty,branch,friend,
            AssociationCategory.Social,"Sample record"),"family required");
        Reject(()=>RelationshipProvenance.Create(family,branch,
            new Relationship(Guid.NewGuid(),a,b,RelationKind.Other),
            AssociationCategory.Social,"Sample record"),"custom link label");
        Reject(()=>RelationshipProvenance.Create(family,branch,
            new Relationship(Guid.NewGuid(),a,b,(RelationKind)999),
            AssociationCategory.Social,"Sample record"),"invalid relation kind");
        Check(typeof(RelationshipProvenance).GetConstructors().Length == 0 &&
            typeof(RelationshipProvenance).GetProperty(nameof(RelationshipProvenance.Category))?
                .SetMethod is null,"immutable provenance category");
        Console.WriteLine("PASS dated and sourced distinct family/social associations");
    }
    static void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
    static void Reject(Action action,string why)
    {
        var caught=false;
        try{action();}catch(ArgumentException){caught=true;}
        Check(caught,why);
    }
}
