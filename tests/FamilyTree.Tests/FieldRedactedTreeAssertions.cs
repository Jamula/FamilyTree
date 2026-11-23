using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class FieldRedactedTreeAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var viewer=Guid.NewGuid();var family=Guid.NewGuid();var branch=Guid.NewGuid();
        var center=new Person(Guid.NewGuid(),"Fictional Center",IsLiving:true);
        var visible=new Person(Guid.NewGuid(),"Fictional Child",IsLiving:false);
        var secret=new Person(Guid.NewGuid(),"PRIVATE PERSON",IsLiving:false);
        var edge=Relationship.Create(center.Id,visible.Id,RelationKind.Parent);
        var hiddenEdge=Relationship.Create(center.Id,secret.Id,RelationKind.Parent);
        var social=Relationship.Create(center.Id,visible.Id,RelationKind.Other,"PRIVATE LABEL");
        var people=new[]{center,visible,secret};
        var relations=new[]{edge,hiddenEdge,social};
        var grants=new[]{
            new BranchVisibilityGrant(viewer,family,branch,MembershipState.Active,
                new HashSet<Guid>{center.Id,visible.Id,secret.Id},
                new HashSet<Guid>{edge.Id,hiddenEdge.Id,social.Id})
        };
        var fields=new FieldPrivacyPolicy(family,new[]{
            new FamilyFieldGrant(viewer,family,center.Id,MembershipState.Active,
                new HashSet<ProfileField>{ProfileField.DisplayName}),
            new FamilyFieldGrant(viewer,family,visible.Id,MembershipState.Active,
                new HashSet<ProfileField>{ProfileField.DisplayName}),
            new FamilyFieldGrant(viewer,family,secret.Id,MembershipState.Active,
                new HashSet<ProfileField>{ProfileField.Contact})
        });
        var view=FieldRedactedTreeQuery.ProjectAsync(viewer,family,branch,center.Id,
            people,relations,new TreeOptions(SocialConnections:true),grants,fields)
            .GetAwaiter().GetResult();
        Check(view is not null && view.Nodes.Select(n=>n.PersonId).ToHashSet()
            .SetEquals(new[]{center.Id,visible.Id}),"hidden field cannot reveal person");
        Check(view!.Edges.Select(e=>e.RelationshipId).ToHashSet()
            .SetEquals(new[]{edge.Id,social.Id}),"only fully authorized edges exposed");
        Check(!typeof(AuthorizedTreeNode).GetProperties().Any(x=>
            x.Name=="IsLiving"||x.Name=="BirthDate"||x.Name=="Contact"),
            "node DTO does not expose sensitive fields");
        Check(!typeof(AuthorizedTreeEdge).GetProperties().Any(x=>
            x.Name=="Label"),"relationship label redacted");
        var denied=FieldRedactedTreeQuery.ProjectAsync(viewer,family,branch,secret.Id,
            people,relations,new TreeOptions(),grants,fields)
            .GetAwaiter().GetResult();
        var missing=FieldRedactedTreeQuery.ProjectAsync(viewer,family,branch,
            Guid.NewGuid(),people,relations,new TreeOptions(),grants,fields)
            .GetAwaiter().GetResult();
        Check(denied is null && missing is null,
            "denied and unknown centers non-enumerating");
        var wrongFieldScope=new FieldPrivacyPolicy(Guid.NewGuid(),[]);
        Check(FieldRedactedTreeQuery.ProjectAsync(viewer,family,branch,center.Id,
            people,relations,new TreeOptions(),grants,wrongFieldScope)
            .GetAwaiter().GetResult() is null,
            "cross-family field and branch grants cannot be combined");
        Check(FieldRedactedTreeQuery.ProjectAsync(viewer,family,Guid.NewGuid(),center.Id,
            people,relations,new TreeOptions(),grants,fields)
            .GetAwaiter().GetResult() is null,
            "wrong branch cannot see center");
        Check(FieldRedactedTreeQuery.ProjectAsync(Guid.NewGuid(),family,branch,center.Id,
            people,relations,new TreeOptions(),grants,fields)
            .GetAwaiter().GetResult() is null,
            "untrusted viewer denied");
        Console.WriteLine("PASS field-redacted graph DTO privacy and cross-scope tests");
    }
    static void Check(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);}
}
