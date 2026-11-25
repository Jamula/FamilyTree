using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class BranchScopedGraphAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var viewer=Guid.NewGuid();var family=Guid.NewGuid();
        var branchA=Guid.NewGuid();var branchB=Guid.NewGuid();
        var a=new Person(Guid.NewGuid(),"Synthetic Center");
        var b=new Person(Guid.NewGuid(),"Synthetic Child");
        var hidden=new Person(Guid.NewGuid(),"Hidden Cousin");
        var first=Relationship.Create(a.Id,b.Id,RelationKind.Parent);
        var cross=Relationship.Create(a.Id,hidden.Id,RelationKind.Parent);
        var people=new[]{a,b,hidden};var relations=new[]{first,cross};
        var grants=new[]{
            new BranchVisibilityGrant(viewer,family,branchA,MembershipState.Active,
                new HashSet<Guid>{a.Id,b.Id},
                new HashSet<Guid>{first.Id,cross.Id}),
            new BranchVisibilityGrant(viewer,family,branchB,MembershipState.Active,
                new HashSet<Guid>{a.Id,hidden.Id},
                new HashSet<Guid>{cross.Id}),
            new BranchVisibilityGrant(viewer,family,Guid.NewGuid(),MembershipState.Paused,
                new HashSet<Guid>{a.Id,b.Id,hidden.Id},
                new HashSet<Guid>{first.Id,cross.Id})
        };
        var tree=BranchScopedTreeQuery.ProjectAsync(viewer,family,branchA,a.Id,
            people,relations,new TreeOptions(),grants).GetAwaiter().GetResult();
        Check(tree is not null && tree.Nodes.Select(x=>x.Person.Id).ToHashSet()
            .SetEquals(new[]{a.Id,b.Id}) && tree.Relationships.Count==1,
            "branch A cannot leak branch B edges or nodes");
        var other=BranchScopedTreeQuery.ProjectAsync(viewer,family,branchB,a.Id,
            people,relations,new TreeOptions(),grants).GetAwaiter().GetResult();
        Check(other is not null && other.Nodes.Select(x=>x.Person.Id).ToHashSet()
            .SetEquals(new[]{a.Id,hidden.Id}),"branch B isolated");
        Check(BranchScopedTreeQuery.ProjectAsync(viewer,Guid.NewGuid(),branchA,a.Id,
            people,relations,new TreeOptions(),grants).GetAwaiter().GetResult() is null,
            "wrong family denied");
        Check(BranchScopedTreeQuery.ProjectAsync(Guid.NewGuid(),family,branchA,a.Id,
            people,relations,new TreeOptions(),grants).GetAwaiter().GetResult() is null,
            "wrong viewer denied");
        Check(BranchScopedTreeQuery.ProjectAsync(viewer,family,branchA,hidden.Id,
            people,relations,new TreeOptions(),grants).GetAwaiter().GetResult() is null,
            "denied and nonexistent centers indistinguishable");
        var authorization = new BranchScopedAuthorization(viewer, family, branchA, grants);
        Check(authorization.CanSeePersonAsync(viewer, a.Id).GetAwaiter().GetResult() &&
            !authorization.CanSeePersonAsync(viewer, Guid.NewGuid()).GetAwaiter().GetResult() &&
            !authorization.CanSeePersonAsync(Guid.NewGuid(), a.Id).GetAwaiter().GetResult(),
            "branch grant requires the trusted viewer and an explicitly visible person");
        Check(!new BranchScopedAuthorization(Guid.Empty, family, branchA, grants)
                .CanSeePersonAsync(Guid.Empty, a.Id).GetAwaiter().GetResult() &&
            !new BranchScopedAuthorization(viewer, Guid.Empty, branchA, grants)
                .CanSeePersonAsync(viewer, a.Id).GetAwaiter().GetResult() &&
            !new BranchScopedAuthorization(viewer, family, Guid.Empty, grants)
                .CanSeePersonAsync(viewer, a.Id).GetAwaiter().GetResult(),
            "empty identity, family and branch identifiers fail closed");
        Check(!authorization.CanSeeRelationshipAsync(viewer,
                Relationship.Create(a.Id, hidden.Id, RelationKind.Parent))
                .GetAwaiter().GetResult() &&
            !authorization.CanSeeRelationshipAsync(Guid.NewGuid(), first)
                .GetAwaiter().GetResult(),
            "relationships require a trusted viewer and visible endpoints");
        Console.WriteLine("PASS per-branch graph isolation and non-enumeration");
    }
    static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
}
