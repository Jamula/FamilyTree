namespace FamilyTree.Domain;

/// <summary>
/// Principal membership is independent of the genealogy Person. Every
/// decision returns a new immutable snapshot; no constructor or setter can
/// directly mint Active status. Storage must enforce invite single-use,
/// optimistic concurrency, token validation and immediate revocation.
/// </summary>
public sealed class MemberEnrollment
{
    public Guid Id { get; }
    public Guid FamilyId { get; }
    public Guid PrincipalId { get; }
    public Guid InvitationId { get; }
    public MembershipState State { get; }
    public Guid? ApprovedBy { get; }
    public bool PausedByAdministrator { get; }

    private MemberEnrollment(Guid id, Guid family, Guid principal, Guid invitation,
        MembershipState state, Guid? approvedBy, bool pausedByAdministrator)
    {
        Id = id; FamilyId = family; PrincipalId = principal;
        InvitationId = invitation; State = state;
        ApprovedBy = approvedBy; PausedByAdministrator = pausedByAdministrator;
    }

    /// <summary>
    /// Requires a validated, accepted invitation from a trusted store.
    /// Does not grant access until an independent administrator approves.
    /// </summary>
    public static MemberEnrollment Request(FamilyInvitation accepted,
        Guid authenticatedPrincipal)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        if (accepted.Stage != InvitationStage.PendingApproval ||
            accepted.IntendedPrincipalId != authenticatedPrincipal)
            throw new UnauthorizedAccessException("Membership request denied.");
        return new(Guid.NewGuid(), accepted.FamilyId, authenticatedPrincipal,
            accepted.Id, MembershipState.PendingApproval, null, false);
    }

    public MemberEnrollment Approve(Guid administratorId, Guid administratorFamily,
        bool administratorAuthorized)
    {
        if ((State != MembershipState.PendingApproval &&
                !(State == MembershipState.Paused && PausedByAdministrator)) ||
            administratorId == Guid.Empty || administratorId == PrincipalId ||
            administratorFamily != FamilyId || !administratorAuthorized)
            throw new UnauthorizedAccessException("Approval denied.");
        return Change(MembershipState.Active, administratorId, false);
    }

    public MemberEnrollment Pause(Guid actorId, Guid actorFamily, bool administratorAuthorized)
    {
        if ((State != MembershipState.Active && State != MembershipState.Paused) ||
            actorFamily != FamilyId || actorId == Guid.Empty ||
            (actorId != PrincipalId && !administratorAuthorized) ||
            (State == MembershipState.Paused &&
                (actorId == PrincipalId || !administratorAuthorized)))
            throw new UnauthorizedAccessException("Pause denied.");
        // An administrator may upgrade a self-pause to suspension; an active
        // member cannot neutralize an existing administrative suspension.
        return Change(MembershipState.Paused, ApprovedBy,
            PausedByAdministrator || (actorId != PrincipalId && administratorAuthorized));
    }

    public MemberEnrollment Resume(Guid actorId)
    {
        if (State != MembershipState.Paused || PausedByAdministrator ||
            actorId != PrincipalId)
            throw new UnauthorizedAccessException("Resumption requires new approval.");
        return Change(MembershipState.Active, ApprovedBy, false);
    }

    public MemberEnrollment Leave(Guid actorId, Guid actorFamily, bool administratorAuthorized)
    {
        if ((State != MembershipState.Active &&
                State != MembershipState.Paused &&
                State != MembershipState.PendingApproval) ||
            actorFamily != FamilyId || actorId == Guid.Empty ||
            (actorId != PrincipalId && !administratorAuthorized))
            throw new UnauthorizedAccessException("Revocation denied.");
        return Change(MembershipState.Left, ApprovedBy, false);
    }

    private MemberEnrollment Change(MembershipState state, Guid? reviewer, bool suspended) =>
        new(Id, FamilyId, PrincipalId, InvitationId, state, reviewer, suspended);
}
