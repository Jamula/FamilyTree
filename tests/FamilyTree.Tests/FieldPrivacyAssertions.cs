using System.Runtime.CompilerServices;
using System.Diagnostics;
using FamilyTree.Domain;
internal static class FieldPrivacyAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var person = Guid.NewGuid(); var member = Guid.NewGuid();
        var family = Guid.NewGuid();
        var grant = new FamilyFieldGrant(member, family, person, MembershipState.Active,
            new HashSet<ProfileField> { ProfileField.DisplayName });
        var policy = new FieldPrivacyPolicy(family, new[] { grant });
        Check(policy.CanRead(member, person, ProfileField.DisplayName), "explicit name allowed");
        Check(!policy.CanRead(member, person, ProfileField.Contact), "contacts private");
        Check(!policy.CanRead(member, person, ProfileField.BirthDate), "dates private");
        Check(!policy.CanRead(member, person, ProfileField.MinorDetails), "minors private");
        Check(!policy.CanRead(member, person, ProfileField.Photograph), "photos private");
        Check(!policy.CanRead(member, Guid.NewGuid(), ProfileField.DisplayName), "other person denied");
        Check(!policy.CanRead(Guid.NewGuid(), person, ProfileField.DisplayName), "other viewer denied");
        Check(!new FieldPrivacyPolicy(Guid.NewGuid(), new[] { grant })
                .CanRead(member, person, ProfileField.DisplayName), "other family denied");
        Check(!new FieldPrivacyPolicy(family, new[] { grant with {
                State = MembershipState.Paused } })
                .CanRead(member, person, ProfileField.DisplayName), "paused membership denied");
        var ids = Enumerable.Range(0, 10_000).Select(_ => Guid.NewGuid()).ToArray();
        var large = new FieldPrivacyPolicy(family, ids.Select(id =>
            new FamilyFieldGrant(member, family, id, MembershipState.Active,
                new HashSet<ProfileField> { ProfileField.DisplayName })));
        var stopwatch = Stopwatch.StartNew();
        for (var pass = 0; pass < 5; pass++)
            foreach (var id in ids)
                Check(large.CanRead(member, id, ProfileField.DisplayName) &&
                    !large.CanRead(member, id, ProfileField.Contact), "indexed independent field checks");
        Check(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "field checks scale with people");
        Console.WriteLine("PASS field and minor-data default privacy checks");
    }
    static void Check(bool x, string msg) { if (!x) throw new Exception(msg); }
}