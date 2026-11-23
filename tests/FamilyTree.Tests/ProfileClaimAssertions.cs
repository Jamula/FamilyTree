using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class ProfileClaimAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var family=Guid.NewGuid(); var member=Guid.NewGuid();
        var moderator=Guid.NewGuid(); var person=Guid.NewGuid();
        var claim=ProfileClaim.Submit(family,member,person,true,true);
        Check(claim.State==ProfileClaimState.Pending,"claim pending");
        Denied(()=>ProfileClaim.Submit(family,member,person,false,true),
            "unjoined principal cannot claim");
        Denied(()=>ProfileClaim.Submit(family,member,person,true,false),
            "invisible person cannot be claimed");
        Denied(()=>claim.Review(member,family,true,true),"self approval");
        Denied(()=>claim.Review(moderator,Guid.NewGuid(),true,true),"cross family");
        Denied(()=>claim.Review(moderator,family,false,true),"non moderator");
        var approved=claim.Review(moderator,family,true,true);
        Check(approved.State==ProfileClaimState.Approved &&
              approved.ReviewedBy==moderator && claim.State==ProfileClaimState.Pending,
              "claim transitions immutable");
        var rejected=claim.Review(moderator,family,true,false);
        Check(rejected.State==ProfileClaimState.Rejected,"reject path");
        var withdrawn=claim.Withdraw(member);
        Check(withdrawn.State==ProfileClaimState.Withdrawn,"claim withdraw");
        Denied(()=>claim.Withdraw(moderator),"other member cannot withdraw");
        try{withdrawn.Review(moderator,family,true,true);
            throw new Exception("withdrawn claim approved");}
        catch(InvalidOperationException ex) when(ex.Message=="Profile claim already decided."){}
        Check(typeof(ProfileClaim).GetConstructors().Length==0 &&
              typeof(ProfileClaim).GetProperty(nameof(ProfileClaim.State))?.SetMethod is null,
              "public callers cannot forge an approved claim");
        Console.WriteLine("PASS profile claims: independent approval, withdrawal and negative cases");
    }
    private static void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
    private static void Denied(Action a,string why)
    {try{a();}catch(UnauthorizedAccessException){return;}throw new InvalidOperationException(why);}
}
