using System.Reflection;
using System.Runtime.CompilerServices;
using FamilyTree.Domain;

internal static class MemberLifecycleAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var now = new DateTimeOffset(2026,11,22,12,0,0,TimeSpan.Zero);
        var family = Guid.NewGuid();
        var issuer = Guid.NewGuid();
        var invited = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var offered = FamilyInvitation.Issue(family,issuer,invited,now.AddDays(1),now);
        Check(typeof(FamilyInvitation).GetConstructors().Length == 0 &&
              typeof(FamilyInvitation).GetProperty(nameof(FamilyInvitation.Stage))!.SetMethod is null,
              "invite state cannot be minted by callers");
        var accepted = offered.Accept(invited,now);
        Deny(() => MemberEnrollment.Request(offered,invited), "offered invite cannot approve");
        Deny(() => MemberEnrollment.Request(accepted,admin), "invitation identity mismatch");
        var pending = MemberEnrollment.Request(accepted,invited);
        Check(pending.State == MembershipState.PendingApproval, "join requires approval");
        Deny(() => pending.Leave(Guid.Empty,family,false), "empty leave actor");
        Deny(() => pending.Approve(invited,family,true), "self approval");
        Deny(() => pending.Approve(admin,Guid.NewGuid(),true), "cross-family review");
        Deny(() => pending.Approve(admin,family,false), "unprivileged review");
        var withdrawn = pending.Leave(invited,family,false);
        var rejected = pending.Leave(admin,family,true);
        Check(withdrawn.State == MembershipState.Left &&
              rejected.State == MembershipState.Left,
              "applicant withdrawal and administrator rejection are terminal");
        Deny(() => withdrawn.Approve(admin,family,true),
            "withdrawn request cannot subsequently be approved");
        Deny(() => rejected.Approve(admin,family,true),
            "rejected request cannot be re-approved");
        var active = pending.Approve(admin,family,true);
        Check(active.State == MembershipState.Active && pending.State == MembershipState.PendingApproval,
            "approval immutable");
        var paused = active.Pause(invited,family,false);
        Check(paused.State == MembershipState.Paused &&
              paused.Resume(invited).State == MembershipState.Active, "member pause/resume");
        var escalated = paused.Pause(admin,family,true);
        Check(escalated.PausedByAdministrator, "administrator may escalate self-pause");
        Deny(() => escalated.Resume(invited), "administrative suspension cannot be evaded");
        var suspended = active.Pause(admin,family,true);
        Deny(() => suspended.Resume(invited), "admin suspension cannot self-resume");
        Deny(() => suspended.Approve(invited,family,true),
            "self re-approval forbidden");
        Check(suspended.Approve(admin,family,true).State == MembershipState.Active,
            "independent authorized admin may restore suspended member");
        var left = suspended.Leave(admin,family,true);
        Check(left.State == MembershipState.Left, "admin revocation");
        Deny(() => left.Approve(admin,family,true), "left membership cannot reactivate");
        Deny(() => active.Pause(Guid.NewGuid(),family,false), "unauthorized pause");
        Check(typeof(MemberEnrollment).GetConstructors().Length == 0 &&
              typeof(MemberEnrollment).GetProperty(nameof(MemberEnrollment.State))!.SetMethod is null,
              "membership state cannot be set externally");
        Console.WriteLine("PASS membership approval, pause, leave and immutable-state checks");
    }

    private static void Check(bool ok,string reason)
    { if(!ok) throw new InvalidOperationException(reason); }
    private static void Deny(Action action,string reason)
    {
        try { action(); }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException(reason);
    }
}
