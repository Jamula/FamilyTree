using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using FamilyTree.Domain;
using FamilyTree.Persistence;

internal static class SqliteGraphGrantStoreAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var folder=Path.Join(Path.GetTempPath(),
            "familytree-synthetic-grants-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path=Path.Join(folder,"fictional.sqlite");
        var key=RandomNumberGenerator.GetBytes(32);
        var family=Guid.NewGuid();var otherFamily=Guid.NewGuid();
        var admin=Guid.NewGuid();var otherAdmin=Guid.NewGuid();
        var member=Guid.NewGuid();var branch=Guid.NewGuid();
        var otherBranch=Guid.NewGuid();var person=new Person(Guid.NewGuid(),"Fictional Privacy Person");
        try
        {
            Expect<ArgumentException>(()=>new SqliteGraphGrantStore("relative.sqlite"),
                "relative path refused");
            using(var data=new SqliteFamilyStore(path,key))
            {
                data.CreateFictionalFamily(family,admin);
                data.CreateFictionalFamily(otherFamily,otherAdmin);
                var invitation=data.IssueInvitation(family,admin,member,
                    DateTimeOffset.UtcNow.AddDays(1),DateTimeOffset.UtcNow);
                data.AcceptInvitation(family,member,invitation,DateTimeOffset.UtcNow);
                var proposal=data.ProposePerson(family,admin,person);
                // Independent moderator is needed to approve a proposed Person.
                // Ordinary pending members cannot review; activate the member
                // to allow them to submit a second proposal instead.
                data.ApproveMember(family,admin,member);
                Expect<UnauthorizedAccessException>(()=>data.ReviewPerson(
                    family,admin,proposal,true),"admin self review denied");
                // Member cannot review because only active administrators review.
                // Use an independently provisioned fictional admin identity.
                var reviewer=Guid.NewGuid();
                var second=data.IssueInvitation(family,admin,reviewer,
                    DateTimeOffset.UtcNow.AddDays(1),DateTimeOffset.UtcNow);
                data.AcceptInvitation(family,reviewer,second,DateTimeOffset.UtcNow);
                data.ApproveMember(family,admin,reviewer);
                // Second member is not an administrator. For this fixture,
                // ask the ordinary member to propose; original admin reviews.
                var submitted=data.ProposePerson(family,member,
                    new Person(Guid.NewGuid(),"Fictional Visible Person"));
                data.ReviewPerson(family,admin,submitted,true);
                person=data.ReadPeople(family,admin).Single();
            }
            var grants=new SqliteGraphGrantStore(path);
            Check(grants.ReadVisiblePersonIds(family,branch,member).Count==0,
                "membership gives no implicit person access");
            Expect<UnauthorizedAccessException>(()=>grants.GrantPerson(
                family,branch,member,admin,person.Id),"ordinary member cannot grant");
            Expect<UnauthorizedAccessException>(()=>grants.GrantPerson(
                otherFamily,branch,admin,member,person.Id),"cross-family grant denied");
            Expect<UnauthorizedAccessException>(()=>grants.GrantPerson(
                family,branch,admin,member,Guid.NewGuid()),"unknown person not granted");
            grants.GrantPerson(family,branch,admin,member,person.Id);
            grants.GrantPerson(family,branch,admin,member,person.Id);
            Check(grants.ReadVisiblePersonIds(family,branch,member).SetEquals([person.Id]),
                "explicit visible person grant is idempotent");
            Check(grants.ReadVisiblePersonIds(family,otherBranch,member).Count==0,
                "other branch sees no person without grant");
            Check(grants.ReadVisiblePersonIds(family,branch,admin).Count==0,
                "administrator has no implicit graph read grant");
            var reopened=new SqliteGraphGrantStore(path);
            Check(reopened.ReadVisiblePersonIds(family,branch,member).Contains(person.Id),
                "grant persists across restart");
            reopened.RevokePerson(family,branch,admin,member,person.Id);
            reopened.RevokePerson(family,branch,admin,member,person.Id);
            Check(reopened.ReadVisiblePersonIds(family,branch,member).Count==0,
                "revoked grant hidden without existence enumeration");
            reopened.GrantPerson(family,branch,admin,member,person.Id);
            using(var data=new SqliteFamilyStore(path,key))
                data.RevokeMember(family,admin,member);
            Expect<UnauthorizedAccessException>(()=>reopened.ReadVisiblePersonIds(
                family,branch,member),"revoked membership cannot see durable grants");
            Expect<UnauthorizedAccessException>(()=>reopened.GrantPerson(
                family,branch,admin,member,person.Id),"revoked membership cannot receive grants");
            Expect<ArgumentException>(()=>reopened.ReadVisiblePersonIds(
                Guid.Empty,branch,admin),"empty family refused");
            Console.WriteLine("PASS persisted branch/person grant isolation, no implicit rights and membership revocation");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(folder,true);
        }
    }
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    static void Expect<T>(Action action,string why) where T:Exception
    {var caught=false;try{action();}catch(T){caught=true;}if(!caught)throw new Exception(why);}
}
