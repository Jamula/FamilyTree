namespace FamilyTree.Domain;

/// <summary>
/// Field visibility never defaults to public. No user-supplied request can
/// mint grants: only a trusted server-side membership store may supply them.
/// </summary>
public enum ProfileField { DisplayName, BirthDate, Contact, Photograph, MinorDetails, LifeEvent }
public sealed record FamilyFieldGrant(Guid PrincipalId, Guid FamilyId, Guid PersonId,
    MembershipState State, IReadOnlySet<ProfileField> AllowedFields);

public sealed class FieldPrivacyPolicy(Guid familyId, IEnumerable<FamilyFieldGrant> trustedGrants)
{
    public Guid FamilyId { get; } = familyId;
    private readonly FamilyFieldGrant[] _grants =
        (trustedGrants ?? throw new ArgumentNullException(nameof(trustedGrants))).ToArray();

    public bool CanRead(Guid principalId, Guid personId, ProfileField field) =>
        principalId != Guid.Empty && personId != Guid.Empty &&
        _grants.Any(g => g.PrincipalId == principalId &&
            g.FamilyId == FamilyId && g.PersonId == personId &&
            g.State == MembershipState.Active && g.AllowedFields.Contains(field));
}