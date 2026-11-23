namespace FamilyTree.Domain;

/// <summary>
/// An immutable invitation transition model. Trusted issuance, token signing,
/// consumption and concurrency enforcement must be provided by the server.
/// </summary>
public enum InvitationStage { Offered, PendingApproval, Declined, Expired }

public sealed class FamilyInvitation
{
    public Guid Id { get; }
    public Guid FamilyId { get; }
    public Guid IssuerPrincipalId { get; }
    public Guid IntendedPrincipalId { get; }
    public DateTimeOffset ExpiresAt { get; }
    public InvitationStage Stage { get; }

    private FamilyInvitation(Guid id, Guid familyId, Guid issuer, Guid intended,
        DateTimeOffset expiry, InvitationStage stage)
    {
        Id = id; FamilyId = familyId; IssuerPrincipalId = issuer;
        IntendedPrincipalId = intended; ExpiresAt = expiry; Stage = stage;
    }

    public static FamilyInvitation Issue(Guid familyId, Guid issuerId, Guid intendedId,
        DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (familyId == Guid.Empty || issuerId == Guid.Empty || intendedId == Guid.Empty ||
            issuerId == intendedId || expiresAt <= now)
            throw new ArgumentException("A distinct principal and future expiry are required.");
        return new(Guid.NewGuid(), familyId, issuerId, intendedId, expiresAt,
            InvitationStage.Offered);
    }

    public FamilyInvitation Accept(Guid authenticatedPrincipal, DateTimeOffset now)
    {
        if (Stage != InvitationStage.Offered || now >= ExpiresAt ||
            authenticatedPrincipal != IntendedPrincipalId)
            throw new UnauthorizedAccessException("Invitation cannot be accepted.");
        return new(Id, FamilyId, IssuerPrincipalId, IntendedPrincipalId, ExpiresAt,
            InvitationStage.PendingApproval);
    }

    public FamilyInvitation Decline(Guid authenticatedPrincipal)
    {
        if (Stage != InvitationStage.Offered ||
            authenticatedPrincipal != IntendedPrincipalId)
            throw new UnauthorizedAccessException("Invitation cannot be declined.");
        return new(Id, FamilyId, IssuerPrincipalId, IntendedPrincipalId, ExpiresAt,
            InvitationStage.Declined);
    }

    public FamilyInvitation Expire(DateTimeOffset now)
    {
        if (Stage != InvitationStage.Offered || now < ExpiresAt)
            throw new InvalidOperationException("Invitation cannot be expired.");
        return new(Id, FamilyId, IssuerPrincipalId, IntendedPrincipalId, ExpiresAt,
            InvitationStage.Expired);
    }
}
