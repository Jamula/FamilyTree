namespace FamilyTree.Domain;

/// <summary>
/// A verified login is not automatically the genealogical Person with the
/// same name. Claim requests are pending until independent approval.
/// The server must load verified actor, active family membership and scoped
/// moderator grants. No HTTP endpoint or database is enabled here.
/// </summary>
public enum ProfileClaimState { Pending, Approved, Rejected, Withdrawn }

public sealed class ProfileClaim
{
    public Guid Id { get; }
    public Guid FamilyId { get; }
    public Guid PersonId { get; }
    public Guid PrincipalId { get; }
    public ProfileClaimState State { get; }
    public Guid? ReviewedBy { get; }

    private ProfileClaim(Guid id, Guid familyId, Guid personId, Guid principalId,
        ProfileClaimState state, Guid? reviewer)
    {
        Id = id; FamilyId = familyId; PersonId = personId; PrincipalId = principalId;
        State = state; ReviewedBy = reviewer;
    }

    public static ProfileClaim Submit(Guid familyId, Guid authenticatedPrincipal,
        Guid existingPersonId, bool activeFamilyMember, bool canViewPerson)
    {
        if (familyId == Guid.Empty || authenticatedPrincipal == Guid.Empty ||
            existingPersonId == Guid.Empty || !activeFamilyMember || !canViewPerson)
            throw new UnauthorizedAccessException("Profile claim denied.");
        return new ProfileClaim(Guid.NewGuid(), familyId, existingPersonId,
            authenticatedPrincipal, ProfileClaimState.Pending, null);
    }

    public ProfileClaim Withdraw(Guid authenticatedPrincipal)
    {
        if (State != ProfileClaimState.Pending || authenticatedPrincipal != PrincipalId)
            throw new UnauthorizedAccessException("Withdrawal denied.");
        return Change(ProfileClaimState.Withdrawn, null);
    }

    public ProfileClaim Review(Guid reviewer, Guid reviewerFamilyId,
        bool canApproveClaims, bool approve)
    {
        if (State != ProfileClaimState.Pending)
            throw new InvalidOperationException("Profile claim already decided.");
        if (reviewer == Guid.Empty || reviewer == PrincipalId ||
            reviewerFamilyId != FamilyId || !canApproveClaims)
            throw new UnauthorizedAccessException("Independent family approval required.");
        return Change(approve ? ProfileClaimState.Approved : ProfileClaimState.Rejected, reviewer);
    }

    private ProfileClaim Change(ProfileClaimState state, Guid? reviewer) =>
        new(Id, FamilyId, PersonId, PrincipalId, state, reviewer);
}
