namespace FamilyTree.Domain;

/// <summary>
/// An explicit trusted grant for one family branch. Never combine people from
/// two branches while constructing a graph, even for the same principal.
/// Persisted family/branch permissions must be read from the server.
/// </summary>
public sealed record BranchVisibilityGrant(
    Guid PrincipalId, Guid FamilyId, Guid BranchId, MembershipState State,
    IReadOnlySet<Guid> VisiblePeople, IReadOnlySet<Guid> VisibleRelationships);

public sealed class BranchScopedAuthorization(
    Guid principalId, Guid familyId, Guid branchId,
    IEnumerable<BranchVisibilityGrant> trustedGrants) : IFamilyAuthorization
{
    private readonly BranchVisibilityGrant[] _grants =
        (trustedGrants ?? throw new ArgumentNullException(nameof(trustedGrants)))
        .Where(g => g.PrincipalId == principalId && g.FamilyId == familyId &&
            g.BranchId == branchId && g.State == MembershipState.Active).ToArray();

    public Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(viewerId == principalId && principalId != Guid.Empty &&
            familyId != Guid.Empty && branchId != Guid.Empty && _grants.Any(g =>
                g.VisiblePeople.Contains(personId)));
    }

    public Task<bool> CanSeeRelationshipAsync(Guid viewerId,
        Relationship relationship, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(relationship);
        return Task.FromResult(viewerId == principalId && principalId != Guid.Empty &&
            familyId != Guid.Empty && branchId != Guid.Empty && _grants.Any(g =>
                g.VisibleRelationships.Contains(relationship.Id) &&
                g.VisiblePeople.Contains(relationship.FromPersonId) &&
                g.VisiblePeople.Contains(relationship.ToPersonId)));
    }
}

public static class BranchScopedTreeQuery
{
    public static Task<PersonTreeProjection?> ProjectAsync(Guid viewerId,
        Guid familyId, Guid branchId, Guid centerPersonId, IEnumerable<Person> people,
        IEnumerable<Relationship> relationships, TreeOptions options,
        IEnumerable<BranchVisibilityGrant> trustedGrants,
        CancellationToken cancellationToken = default)
        => PersonTreeProjector.ProjectAsync(viewerId, centerPersonId, people,
            relationships, options,
            new BranchScopedAuthorization(viewerId, familyId, branchId, trustedGrants),
            cancellationToken);
}
