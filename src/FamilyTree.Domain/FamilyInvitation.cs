namespace FamilyTree.Domain;

/// <summary>
/// Identity invitation state machine. Security-critical proof of identity,
/// invitation signatures, and persistence belong to a trusted server.
/// </summary>
public enum InvitationStage { Offered, PendingApproval, Declined, Expired }

public sealed record FamilyInvitation(Guid Id, Guid FamilyId, Guid IssuerPrincipalId,
    Guid IntendedPrincipalId, DateTimeOffset ExpiresAt, InvitationStage Stage)
{
    public static FamilyInvitation Issue(Guid familyId, Guid issuerId, Guid intendedId,
        DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (familyId == Guid.Empty || issuerId == Guid.Empty || intendedId == Guid.Empty ||
            issuerId == intendedId || expiresAt <= now)
            throw new ArgumentException("A valid distinct principal and future expiry are required.");
        return new(Guid.NewGuid(), familyId, issuerId, intendedId, expiresAt,
            InvitationStage.Offered);
    }

    public FamilyInvitation Accept(Guid authenticatedPrincipal, DateTimeOffset now)
    {
        if (Stage != InvitationStage.Offered || now >= ExpiresAt ||
            authenticatedPrincipal != IntendedPrincipalId)
            throw new UnauthorizedAccessException("Invitation cannot be accepted.");
        return this with { Stage = InvitationStage.PendingApproval };
    }

    public FamilyInvitation Decline(Guid authenticatedPrincipal)
    {
        if (Stage != InvitationStage.Offered ||
            authenticatedPrincipal != IntendedPrincipalId)
            throw new UnauthorizedAccessException("Invitation cannot be declined.");
        return this with { Stage = InvitationStage.Declined };
    }

    public FamilyInvitation Expire(DateTimeOffset now)
    {
        if (Stage != InvitationStage.Offered || now < ExpiresAt)
            throw new InvalidOperationException("Invitation cannot be expired.");
        return this with { Stage = InvitationStage.Expired };
    }
}