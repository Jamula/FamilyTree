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
    private readonly HashSet<(Guid Principal, Guid Person, ProfileField Field)> _grants =
        (trustedGrants ?? throw new ArgumentNullException(nameof(trustedGrants)))
        .Where(g => g.FamilyId == familyId && g.State == MembershipState.Active)
        .SelectMany(g => g.AllowedFields.Select(field => (g.PrincipalId, g.PersonId, field)))
        .ToHashSet();

    public bool CanRead(Guid principalId, Guid personId, ProfileField field) =>
        principalId != Guid.Empty && personId != Guid.Empty &&
        _grants.Contains((principalId, personId, field));
}