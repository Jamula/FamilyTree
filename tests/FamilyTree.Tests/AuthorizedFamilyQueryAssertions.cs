using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class AuthorizedFamilyQueryAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var viewer = Guid.NewGuid();
        var memberFamily = Guid.NewGuid();
        var otherFamily = Guid.NewGuid();
        var publicMember = new Person(Guid.NewGuid(), "Ada Fictional");
        var hiddenMember = new Person(Guid.NewGuid(), "Ada Hidden");
        var otherTenant = new Person(Guid.NewGuid(), "Ada Other Family");
        var noFieldGrant = new Person(Guid.NewGuid(), "Ada Redacted");
        var people = new[] { publicMember, hiddenMember, otherTenant, noFieldGrant };
        var grants = new[] {
            new ScopedFamilyGrant(viewer, memberFamily, MembershipState.Active,
                new HashSet<Guid> { publicMember.Id, noFieldGrant.Id },
                new HashSet<Guid>()),
            new ScopedFamilyGrant(viewer, otherFamily, MembershipState.Active,
                new HashSet<Guid> { otherTenant.Id },
                new HashSet<Guid>())
        };
        var fields = new FieldPrivacyPolicy(memberFamily, new[] {
            new FamilyFieldGrant(viewer, memberFamily, publicMember.Id,
                MembershipState.Active, new HashSet<ProfileField> { ProfileField.DisplayName }),
            new FamilyFieldGrant(viewer, memberFamily, noFieldGrant.Id,
                MembershipState.Active, new HashSet<ProfileField> { ProfileField.Contact })
        });
        var auth = new MembershipScopedAuthorization(memberFamily, grants);
        var result = AuthorizedFamilyQuery.SearchAsync(viewer, people, "Ada", auth, fields)
            .GetAwaiter().GetResult();
        Check(result.Count == 1 && result[0].PersonId == publicMember.Id,
            "search must not enumerate hidden, cross-family or field-redacted persons");
        Check(typeof(AuthorizedPersonSummary).GetProperties().Select(p => p.Name)
            .ToHashSet().SetEquals(new[] { "PersonId", "DisplayName" }),
            "search output must exclude sensitive fields");
        Check(AuthorizedFamilyQuery.SearchAsync(Guid.NewGuid(), people, "Ada", auth, fields)
            .GetAwaiter().GetResult().Count == 0, "unknown principal denied");
        Check(AuthorizedFamilyQuery.GetAsync(viewer, hiddenMember.Id, people, auth, fields)
            .GetAwaiter().GetResult() is null &&
              AuthorizedFamilyQuery.GetAsync(viewer, Guid.NewGuid(), people, auth, fields)
            .GetAwaiter().GetResult() is null,
            "hidden and unknown lookups return identical result");
        Check(AuthorizedFamilyQuery.SearchAsync(viewer, people, "A", auth, fields)
            .GetAwaiter().GetResult().Count == 0, "one-character enumeration denied");
        Check(AuthorizedFamilyQuery.SearchAsync(viewer, people, new string('x', 81), auth, fields)
            .GetAwaiter().GetResult().Count == 0, "oversized search rejected");
        Console.WriteLine("PASS authorized search and enumeration/privacy negative tests");
    }

    private static void Check(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }
}
