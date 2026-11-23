using System.Runtime.CompilerServices;
using FamilyTree.Domain;
internal static class InvitationAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var now = new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
        var admin = Guid.NewGuid();
        var member = Guid.NewGuid();
        var invite = FamilyInvitation.Issue(Guid.NewGuid(), admin, member,
            now.AddHours(2), now);
        var pending = invite.Accept(member, now.AddMinutes(1));
        Check(pending.Stage == InvitationStage.PendingApproval &&
              invite.Stage == InvitationStage.Offered, "accept requires separate approval");
        Denied(() => invite.Accept(admin, now), "issuer cannot impersonate invitee");
        Denied(() => invite.Accept(member, now.AddHours(2)), "expiry boundary denied");
        Denied(() => pending.Accept(member, now), "single use");
        Check(invite.Decline(member).Stage == InvitationStage.Declined,
              "decline transition");
        Check(invite.Expire(now.AddHours(2)).Stage == InvitationStage.Expired,
              "expiry transition");
        Console.WriteLine("PASS invitation lifecycle safety checks");
    }
    private static void Check(bool pass, string msg) { if (!pass) throw new Exception(msg); }
    private static void Denied(Action action, string msg)
    { try { action(); } catch (UnauthorizedAccessException) { return; }
      throw new Exception(msg); }
}