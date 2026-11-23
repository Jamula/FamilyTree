using FamilyTree.Domain;

namespace FamilyTree.Domain;

/// <summary>
/// An application principal is not a genealogy Person. Grants are scoped to one
/// family; no grant is implied by a login, invitation, or administrator role.
/// This is an in-memory authorization primitive, NOT an OIDC/session system.
/// </summary>
public sealed record ScopedFamilyGrant(
    Guid PrincipalId,
    Guid FamilyId,
    MembershipState State,
    IReadOnlySet<Guid> VisiblePersonIds,
    IReadOnlySet<Guid> VisibleRelationshipIds);

/// <summary>
/// Explicit allowlists are deliberately fail-closed. Callers must construct
/// grants from a trusted server-side membership store, never request payloads.
/// </summary>
public sealed class MembershipScopedAuthorization(
    Guid familyId, IEnumerable<ScopedFamilyGrant> trustedGrants) : IFamilyAuthorization
{
    private readonly ScopedFamilyGrant[] _grants =
        (trustedGrants ?? throw new ArgumentNullException(nameof(trustedGrants))).ToArray();

    public Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Active(viewerId).Any(g => g.VisiblePersonIds.Contains(personId)));
    }

    public Task<bool> CanSeeRelationshipAsync(Guid viewerId, Relationship relationship,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(relationship);
        // A visible relationship must not disclose either hidden endpoint.
        var allowed = Active(viewerId).Any(g =>
            g.VisibleRelationshipIds.Contains(relationship.Id) &&
            g.VisiblePersonIds.Contains(relationship.FromPersonId) &&
            g.VisiblePersonIds.Contains(relationship.ToPersonId));
        return Task.FromResult(allowed);
    }

    private IEnumerable<ScopedFamilyGrant> Active(Guid viewerId) =>
        _grants.Where(g => g.PrincipalId == viewerId &&
                           g.FamilyId == familyId &&
                           g.State == MembershipState.Active);
}
