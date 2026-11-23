using FamilyTree.Domain;

internal static class MembershipAuthorizationAssertions
{
    public static async Task RunAsync()
    {
        var family = Guid.NewGuid();
        var otherFamily = Guid.NewGuid();
        var user = Guid.NewGuid();
        var subject = Guid.NewGuid();
        var other = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var relationship = Relationship.Create(subject, other, RelationKind.Parent);
        var leak = Relationship.Create(subject, hidden, RelationKind.Friend);
        var allowedPeople = new HashSet<Guid> { subject, other };
        var allowedLinks = new HashSet<Guid> { relationship.Id, leak.Id };
        var grants = new[]
        {
            new ScopedFamilyGrant(user, family, MembershipState.Invited, allowedPeople, allowedLinks),
            new ScopedFamilyGrant(user, family, MembershipState.Paused, allowedPeople, allowedLinks),
            new ScopedFamilyGrant(user, otherFamily, MembershipState.Active,
                new HashSet<Guid> { hidden }, new HashSet<Guid> { leak.Id }),
            new ScopedFamilyGrant(user, family, MembershipState.Active, allowedPeople, allowedLinks)
        };
        var policy = new MembershipScopedAuthorization(family, grants);
        Check(await policy.CanSeePersonAsync(user, subject), "active scoped subject visible");
        Check(!await policy.CanSeePersonAsync(user, hidden), "cross-tenant hidden");
        Check(!await policy.CanSeePersonAsync(Guid.NewGuid(), subject), "unknown principal denied");
        Check(await policy.CanSeeRelationshipAsync(user, relationship), "explicit visible link");
        Check(!await policy.CanSeeRelationshipAsync(user, leak), "hidden edge endpoint denied");

        var inactive = new MembershipScopedAuthorization(family,
            grants.Where(g => g.State != MembershipState.Active));
        Check(!await inactive.CanSeePersonAsync(user, subject), "invited and paused are denied");

        var tree = await PersonTreeProjector.ProjectAsync(user, subject,
            new[] { new Person(subject, "Visible"), new Person(other, "Related"),
                    new Person(hidden, "Invisible") },
            new[] { relationship, leak }, new TreeOptions(), policy);
        Check(tree is not null && tree.Nodes.All(x => x.Person.Id != hidden) &&
              tree.Relationships.All(x => x.Id != leak.Id),
              "projector cannot leak hidden nodes/links");
        Check(await PersonTreeProjector.ProjectAsync(user, hidden,
            new[] { new Person(subject, "Visible"), new Person(hidden, "Invisible") },
            new[] { leak }, new TreeOptions(), policy) is null,
            "invisible and unknown centers indistinguishable");
        Console.WriteLine("PASS membership scoped authorization negative tests");
    }

    private static void Check(bool passed, string reason)
    {
        if (!passed) throw new InvalidOperationException(reason);
    }
}
